#Requires -Version 7.0
<#
.SYNOPSIS
    将 NewLife 仓库的 publish*.yml 从 NuGet API Key 迁移到 Trusted Publishing（GitHub OIDC）。

.DESCRIPTION
    nuget.org 自 2026-11-01 起已让所有旧 API Key 过期，新 Key 最长只能 30 天。
    本脚本把各仓库 .github/workflows/publish*.yml 中的长期 Key 改为 OIDC 临时 Key：

      1. 补 id-token: write 权限（原本没有 permissions 块时补全整块）；
      2. 在推送 nuget.org 的作业中插入 NuGet/login@v1 步骤（id 为 nuget-login）；
      3. 把 --api-key ${{ secrets.nugetKey }} 替换为
         --api-key ${{ steps.nuget-login.outputs.NUGET_API_KEY }}。

    默认只预览，不改文件。确认后使用 -Apply 写入；再用 -Commit / -Push 提交推送。
    同时导出需要到 nuget.org 逐条创建的 Trusted Publishing 策略清单（CSV）。

    注意：nuget.org 目前没有策略管理 API，策略必须在网页上创建
    （https://www.nuget.org/account/trustedpublishing）。新建策略 7 天内未使用会失效，
    建议先建策略、再推送工作流。

.PARAMETER Root
    仓库所在的父目录，默认 C:\X。只处理该目录下带 .git 的一级子目录。

.PARAMETER Repos
    只处理指定仓库名（可多个）。不指定时处理 Root 下全部仓库。

.PARAMETER NuGetUser
    nuget.org 上的账号（策略创建者/包所有者），默认 NewLife。策略清单中的 RepositoryOwner
    由各仓库的 GitHub 远端自动解析。

.PARAMETER PolicyCsv
    导出的策略清单路径，默认 <Root>\nuget-trusted-policies.csv。

.PARAMETER PolicyScript
    导出的浏览器控制台脚本路径，默认 <Root>\nuget-create-policies.js。
    登录 nuget.org 后打开 Trusted Publishing 页面，把脚本内容粘贴到控制台即可批量创建策略。

.PARAMETER Apply
    真正写入文件（默认只预览）。

.PARAMETER Commit
    写入后按仓库提交变更（只提交被修改的工作流文件）。需同时指定 -Apply。

.PARAMETER Push
    提交后推送。默认推到 origin（自建源）；没有 origin 时推到 GitHub 远端。
    只推自建源、稍后再镜像到 GitHub 时保持默认即可。

.PARAMETER PushRemote
    指定推送远端名，覆盖默认的 origin。

.PARAMETER Force
    允许在非 master/main 分支上提交/推送（例如 NewLife.Core 的 dev 分支）。

.EXAMPLE
    # 预览全部仓库的改动
    pwsh -File .\tools\migrate-nuget-trusted-publishing.ps1 -Root C:\X

.EXAMPLE
    # 只改 NewLife.Audio 和 NewLife.Redis，并提交推送到自建 origin
    pwsh -File .\tools\migrate-nuget-trusted-publishing.ps1 -Root C:\X -Repos NewLife.Audio,NewLife.Redis -Apply -Commit -Push
#>
[CmdletBinding()]
param(
    [string]$Root = 'C:\X',
    [string[]]$Repos,
    [string]$NuGetUser = 'NewLife',
    [string]$PolicyCsv,
    [string]$PolicyScript,
    [switch]$Apply,
    [switch]$Commit,
    [switch]$Push,
    [string]$PushRemote,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
if ($Commit -and -not $Apply) { throw '使用 -Commit 前必须先指定 -Apply' }
if ($Push -and -not $Commit) { throw '使用 -Push 前必须先指定 -Commit' }
if (-not $PolicyCsv) { $PolicyCsv = Join-Path $Root 'nuget-trusted-policies.csv' }
if (-not $PolicyScript) { $PolicyScript = Join-Path $Root 'nuget-create-policies.js' }

$Utf8NoBom = [System.Text.UTF8Encoding]::new($false)

function New-PolicyRow([string]$NuGetUser, [string]$Owner, [string]$Repo, [string]$WorkflowFile) {
    [pscustomobject]@{
        NuGetOwner      = $NuGetUser
        RepositoryOwner = $Owner
        Repository      = $Repo
        WorkflowFile    = $WorkflowFile
        Environment     = ''
        PolicyName      = "$Repo-$([System.IO.Path]::GetFileNameWithoutExtension($WorkflowFile))"
        Scopes          = 'Push (Push new packages and package versions)'
        GlobPatterns    = '*'
    }
}

function Get-Newline([string]$Text) {
    if ($Text.Contains("`r`n")) { return "`r`n" }
    return "`n"
}

function Add-IdTokenPermission([string]$Text) {
    if ($Text -match '(?m)^\s+id-token\s*:\s*write\s*$') { return $Text }

    # 已有工作流级 permissions 块：紧跟 permissions: 行插入
    if ($Text -match '(?m)^permissions\s*:\s*$') {
        $re = [regex]::new('(?m)^(permissions\s*:\s*)$')
        return $re.Replace($Text, "`$1`n  id-token: write", 1)
    }

    # 已有作业级 permissions 块：按同样缩进插入
    if ($Text -match '(?m)^    permissions\s*:\s*$') {
        $re = [regex]::new('(?m)^(    permissions\s*:\s*)$')
        return $re.Replace($Text, "`$1`n      id-token: write", 1)
    }

    if ($Text -match '(?m)^permissions\s*:\s*\S') {
        throw 'permissions 使用了行内写法，请手工迁移'
    }

    # 完全没有 permissions 块：补一整块，避免只写 id-token 导致其余权限变成 none
    $contents = 'read'
    if ($Text -match '(?mi)gh\s+release|action-gh-release|actions/create-release') { $contents = 'write' }

    $lines = @('permissions:', "  contents: $contents")
    if ($Text -match 'nuget\.pkg\.github\.com') { $lines += '  packages: write' }
    $lines += '  id-token: write'
    $block = ($lines -join "`n") + "`n`n"

    if ($Text -notmatch '(?m)^jobs\s*:\s*$') { throw '未找到 jobs: 节点，无法插入 permissions 块' }
    $re = [regex]::new('(?m)^(jobs\s*:\s*)$')
    return $re.Replace($Text, [System.Text.RegularExpressions.MatchEvaluator] { param($m2) $block + $m2.Value }, 1)
}

function Add-LoginStep([string]$Text) {
    if ($Text -match 'NuGet/login@') { return $Text }

    $keyMatch = [regex]::Match($Text, '\$\{\{\s*secrets\.nugetKey\s*\}\}')
    if (-not $keyMatch.Success) { return $Text }

    # 从 Key 出现位置往前找最近的步骤起始行（- name/- uses/- run/- id）
    $head = $Text.Substring(0, $keyMatch.Index)
    $stepMatches = [regex]::Matches($head, '(?m)^(?<indent>[ \t]*)-(?<rest>[ \t]+)(?<kind>name|uses|run|id)\s*:')
    if ($stepMatches.Count -eq 0) { throw '未找到包含 nugetKey 的步骤起始行，无法插入登录步骤' }

    $step = $stepMatches[$stepMatches.Count - 1]
    $indent = $step.Groups['indent'].Value
    $insertAt = $step.Index
    $login = @(
        "${indent}- name: NuGet login (OIDC)"
        "${indent}  id: nuget-login"
        "${indent}  uses: NuGet/login@v1"
        "${indent}  with:"
        "${indent}    user: $NuGetUser"
    ) -join "`n"

    return $Text.Insert($insertAt, $login + "`n")
}

function Convert-Workflow([string]$Text) {
    $newline = Get-Newline $Text
    $work = $Text.Replace("`r`n", "`n")

    $work = Add-IdTokenPermission $work
    $work = Add-LoginStep $work
    $work = [regex]::Replace($work, '\$\{\{\s*secrets\.nugetKey\s*\}\}', '${{ steps.nuget-login.outputs.NUGET_API_KEY }}')

    if ($work -match 'secrets\.nugetKey') { throw '仍残留 secrets.nugetKey' }
    if ($work -notmatch '(?m)^\s+id-token\s*:\s*write\s*$') { throw '缺少 id-token: write' }
    if (([regex]::Matches($work, 'NuGet/login@')).Count -ne 1) { throw 'NuGet/login 步骤数量不为 1' }

    if ($newline -eq "`r`n") { $work = $work.Replace("`n", "`r`n") }
    return $work
}

function Get-GitHubRemoteInfo([string]$RepoPath) {
    $remotes = & git -C $RepoPath remote -v 2>$null
    foreach ($line in $remotes) {
        if ($line -match '^(?<remote>\S+)\s+(?:https://github\.com/|git@github\.com:)(?<owner>[^/\s]+)/(?<repo>[^/\s]+?)(?:\.git)?(?=\s)') {
            return [pscustomobject]@{
                Remote = $Matches['remote']
                Owner  = $Matches['owner']
                Name   = $Matches['repo']
            }
        }
    }
    return $null
}

# ---------- 收集仓库 ----------
$repoDirs = Get-ChildItem -Path $Root -Directory |
    Where-Object { Test-Path (Join-Path $_.FullName '.git') } |
    Sort-Object Name
if ($Repos) {
    $Repos = @($Repos | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    $repoDirs = $repoDirs | Where-Object { $Repos -contains $_.Name }
    $missing = $Repos | Where-Object { $name = $_; -not ($repoDirs.Name -contains $name) }
    foreach ($name in $missing) { Write-Warning "本地未找到仓库 $name（可先 git clone 再运行）" }
}

# 只处理有 GitHub 远端的仓库，并按远端去重（同一仓库可能存在多个本地克隆）
$repoEntries = [System.Collections.Generic.List[object]]::new()
$seenRemote = @{}
foreach ($dir in $repoDirs) {
    $info = Get-GitHubRemoteInfo $dir.FullName
    if (-not $info) {
        Write-Warning "[$($dir.Name)] 未找到 GitHub 远端，已跳过"
        continue
    }

    $key = "$($info.Owner)/$($info.Name)".ToLowerInvariant()
    if ($seenRemote.ContainsKey($key)) {
        $old = $seenRemote[$key]
        if ($dir.Name -ieq $info.Name -and $old.Dir.Name -ine $info.Name) {
            [void]$repoEntries.Remove($old)
            $entry = [pscustomobject]@{ Dir = $dir; Gh = $info }
            $repoEntries.Add($entry)
            $seenRemote[$key] = $entry
            Write-Host "[$($dir.Name)] 与 $($old.Dir.Name) 是同一 GitHub 仓库，改用本目录"
        }
        else {
            Write-Warning "[$($dir.Name)] 与 $($old.Dir.Name) 是同一 GitHub 仓库，已跳过"
        }
        continue
    }

    $entry = [pscustomobject]@{ Dir = $dir; Gh = $info }
    $seenRemote[$key] = $entry
    $repoEntries.Add($entry)
}

$results = [System.Collections.Generic.List[object]]::new()
$policies = [System.Collections.Generic.List[object]]::new()

foreach ($entry in $repoEntries) {
    $repo = $entry.Dir
    $gh = $entry.Gh
    $workflowDir = Join-Path $repo.FullName '.github\workflows'
    if (-not (Test-Path $workflowDir)) { continue }

    $policyOwner = $gh.Owner
    $policyRepo = $gh.Name

    $files = Get-ChildItem -Path $workflowDir -File |
        Where-Object { $_.Name -match '^publish.*\.ya?ml$' } |
        Sort-Object Name
    if (-not $files) { continue }

    foreach ($file in $files) {
        $text = [System.IO.File]::ReadAllText($file.FullName)
        if ($text -notmatch 'secrets\.nugetKey') {
            if ($text -match 'NuGet/login@') {
                $results.Add([pscustomobject]@{
                    Repository = $repo.Name
                    Workflow   = $file.Name
                    Path       = $file.FullName
                    Status     = '已迁移'
                    Message    = ''
                    NewText    = $text
                })
                $policies.Add((New-PolicyRow $NuGetUser $policyOwner $policyRepo $file.Name))
            }
            continue
        }

        try {
            $newText = Convert-Workflow $text
            $results.Add([pscustomobject]@{
                Repository = $repo.Name
                Workflow   = $file.Name
                Path       = $file.FullName
                Status     = '待修改'
                Message    = ''
                NewText    = $newText
            })
            $policies.Add((New-PolicyRow $NuGetUser $policyOwner $policyRepo $file.Name))
        }
        catch {
            $results.Add([pscustomobject]@{
                Repository = $repo.Name
                Workflow   = $file.Name
                Path       = $file.FullName
                Status     = '失败'
                Message    = $_.Exception.Message
                NewText    = $null
            })
        }
    }
}

# ---------- 预览 / 写入 ----------
foreach ($r in $results) {
    if ($r.Status -ne '待修改') { continue }

    if (-not $Apply) {
        $r.Status = '预览'
        continue
    }

    [System.IO.File]::WriteAllText($r.Path, $r.NewText, $Utf8NoBom)
    $r.Status = '已修改'
}

# ---------- 提交 / 推送 ----------
if ($Commit) {
    $groups = $results | Where-Object { $_.Status -in @('已修改', '已迁移') } | Group-Object Repository
    foreach ($g in $groups) {
        $entry = $repoEntries | Where-Object { $_.Dir.Name -eq $g.Name } | Select-Object -First 1
        $repo = $entry.Dir
        $branch = (& git -C $repo.FullName branch --show-current).Trim()
        if (-not $Force -and $branch -notin @('master', 'main')) {
            Write-Warning "[$($g.Name)] 当前分支是 $branch，非 master/main，已跳过（-Force 可强制）"
            continue
        }

        $paths = $g.Group | ForEach-Object {
            [System.IO.Path]::GetRelativePath($repo.FullName, $_.Path).Replace('\', '/')
        }
        $changedPaths = @(& git -C $repo.FullName status --porcelain -- $paths)
        if (-not $changedPaths) {
            Write-Host "[$($g.Name)] 工作流已提交过，跳过"
            continue
        }

        # 只提交本次迁移涉及的文件；其它未提交改动保持原样，不纳入本次提交
        $otherDirty = @(& git -C $repo.FullName status --porcelain) | Where-Object {
            $p = $_.Substring(3).Trim('"')
            -not ($paths -contains $p)
        }
        if ($otherDirty) {
            Write-Host "[$($g.Name)] 工作区还有 $($otherDirty.Count) 个其它未提交文件，本次只提交工作流"
        }

        & git -C $repo.FullName add -- $paths
        & git -C $repo.FullName commit -m 'ci: NuGet 发布迁移到 Trusted Publishing' -- $paths | Out-Host
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "[$($g.Name)] 提交失败，已跳过"
            continue
        }

        if ($Push) {
            $targetRemote = $PushRemote
            if (-not $targetRemote) {
                $names = @(& git -C $repo.FullName remote)
                $targetRemote = if ($names -contains 'origin') { 'origin' } else { $entry.Gh.Remote }
            }
            & git -C $repo.FullName push $targetRemote HEAD | Out-Host
            if ($LASTEXITCODE -ne 0) { Write-Warning "[$($g.Name)] 推送到 $targetRemote 失败" }
        }
    }
}

# ---------- 输出 ----------
$results | Select-Object Repository, Workflow, Status, Message | Format-Table -AutoSize

$policies = @($policies | Sort-Object NuGetOwner, RepositoryOwner, Repository, WorkflowFile, Environment -Unique)
$policies | Sort-Object Repository, WorkflowFile | Export-Csv -LiteralPath $PolicyCsv -NoTypeInformation -Encoding utf8

$policyJson = if ($policies.Count -eq 0) { '[]' } else {
    @($policies | ForEach-Object {
        [ordered]@{
            n     = $_.PolicyName
            owner = $_.NuGetOwner
            o     = $_.RepositoryOwner
            r     = $_.Repository
            w     = $_.WorkflowFile
            e     = $_.Environment
            g     = $_.GlobPatterns
        }
    }) | ConvertTo-Json -Depth 4 -Compress -AsArray
}

$jsTemplate = @'
// 由 tools/migrate-nuget-trusted-publishing.ps1 生成。
// 用法：登录 https://www.nuget.org/account/trustedpublishing ，F12 打开浏览器控制台，粘贴本文件全部内容后回车。
// 脚本只在当前页面用你的登录会话调用 NuGet 自己的创建策略接口，不包含任何凭据。
(async () => {
  const POLICIES = __POLICIES__;
  const page = window.initialData;
  const tokenField = document.querySelector('#AntiForgeryForm input[name=__RequestVerificationToken]');
  if (!page || !page.GenerateUrl || !tokenField) {
    console.error('请先登录并打开 https://www.nuget.org/account/trustedpublishing 页面再执行');
    return;
  }

  const existing = new Set((page.Policies || []).map(p => {
    const d = p.PolicyDetails || {};
    return `${d.RepositoryOwner}/${d.Repository}/${d.WorkflowFile}`.toLowerCase();
  }));

  // 公共仓库可以顺手带上 GitHub 仓库 ID，策略立即永久生效；拿不到 ID 时 NuGet 会让它先临时生效 7 天。
  const idCache = new Map();
  async function getRepoIds(owner, repo) {
    const key = `${owner}/${repo}`.toLowerCase();
    if (idCache.has(key)) return idCache.get(key);

    let ids = {};
    try {
      const res = await fetch(`https://api.github.com/repos/${owner}/${repo}`, {
        headers: { Accept: 'application/vnd.github+json' }
      });
      if (res.ok) {
        const data = await res.json();
        ids = { RepositoryOwnerId: String(data.owner.id), RepositoryId: String(data.id) };
      }
    } catch {
      // 忽略：没有 ID 时策略进入 7 天临时状态，首次发布成功后自动转永久
    }
    idCache.set(key, ids);
    return ids;
  }

  let ok = 0, skipped = 0, failed = 0;
  for (const p of POLICIES) {
    const key = `${p.o}/${p.r}/${p.w}`.toLowerCase();
    if (existing.has(key)) {
      console.log('跳过已存在:', key);
      skipped++;
      continue;
    }

    const ids = await getRepoIds(p.o, p.r);
    const body = new URLSearchParams();
    body.set('__RequestVerificationToken', tokenField.value);
    body.set('policyName', p.n);
    body.set('owner', p.owner);
    body.set('criteria', JSON.stringify({
      Name: 'GitHubActions',
      RepositoryOwner: p.o,
      Repository: p.r,
      WorkflowFile: p.w,
      Environment: p.e || '',
      ...ids
    }));
    body.set('publisherType', 'GitHubActions');
    body.append('policyScopes[]', 'package:push');
    body.append('policySubjects[]', p.g || '*');

    try {
      const res = await fetch(page.GenerateUrl, { method: 'POST', credentials: 'same-origin', body });
      if (res.ok) {
        ok++;
        console.log('已创建:', key);
      } else {
        failed++;
        console.error('创建失败:', key, res.status, await res.text());
      }
    } catch (e) {
      failed++;
      console.error('请求失败:', key, e);
    }
    await new Promise(r => setTimeout(r, 600));
  }
  console.log(`完成：成功 ${ok}，跳过 ${skipped}，失败 ${failed}`);
})();
'@

[System.IO.File]::WriteAllText($PolicyScript, $jsTemplate.Replace('__POLICIES__', $policyJson), $Utf8NoBom)

$changed = @($results | Where-Object { $_.Status -in @('预览', '已修改') })
Write-Host ''
Write-Host "工作流：$($changed.Count) 个$(if (-not $Apply) { '待修改（预览模式，加 -Apply 写入）' } else { '已修改' })"
Write-Host "策略清单：$($policies.Count) 条 → $PolicyCsv"
Write-Host "批量建策略脚本：$PolicyScript"
Write-Host '请先登录 nuget.org 打开 Trusted Publishing 页面，把上面的 .js 内容粘贴到浏览器控制台执行；'
Write-Host '或按 CSV 手工创建：Package Owner=NewLife，Scopes 勾选 Push 并选 “Push new packages and package versions”，Glob Patterns 填 *。'
Write-Host '策略新建后 7 天内必须被工作流使用，否则会失效；只改了自建源、还没镜像到 GitHub 时不要提前建策略。'
Write-Host '建议按仓库分批：镜像到 GitHub 前用 -Repos <仓库名> 生成该仓库的策略并创建，然后立即发布验证。'
