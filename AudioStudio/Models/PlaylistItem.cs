using System;

namespace AudioStudio.Models;

/// <summary>播放列表项</summary>
public class PlaylistItem
{
    /// <summary>文件路径</summary>
    public String FilePath { get; set; } = String.Empty;

    /// <summary>文件名</summary>
    public String FileName { get; set; } = String.Empty;

    /// <summary>标题（取自元数据或文件名）</summary>
    public String Title { get; set; } = String.Empty;

    /// <summary>艺术家</summary>
    public String Artist { get; set; } = String.Empty;

    /// <summary>时长</summary>
    public TimeSpan Duration { get; set; }

    /// <summary>时长文本</summary>
    public String DurationText => Duration.TotalHours >= 1
        ? Duration.ToString(@"h\:mm\:ss")
        : Duration.ToString(@"mm\:ss");

    /// <summary>文件大小</summary>
    public Int64 FileSize { get; set; }

    /// <summary>文件大小文本</summary>
    public String FileSizeText
    {
        get
        {
            if (FileSize < 1024) return $"{FileSize} B";
            if (FileSize < 1024 * 1024) return $"{FileSize / 1024.0:F1} KB";
            if (FileSize < 1024 * 1024 * 1024) return $"{FileSize / (1024.0 * 1024):F1} MB";
            return $"{FileSize / (1024.0 * 1024 * 1024):F2} GB";
        }
    }
}
