using System.Runtime.InteropServices;

namespace NewLife.Audio.AAC;

/// <summary>AAC编码器</summary>
public class FaacEncoder : DisposeBase
{
    private IntPtr _handle = IntPtr.Zero;
    public Int32 InputSamples { get; private set; }
    public Int32 MaxOutput { get; private set; }
    public Int32 FrameSize { get; private set; }
    private Stream _cache = new MemoryStream();

    public FaacEncoder(Int32 sampleRate, Int32 channels, Int32 sampleBit, Boolean adts = false)
    {
        var inputSampleBytes = new Byte[4];
        var maxOutputBytes = new Byte[4];

        _handle = FaacEncOpen(sampleRate, channels, inputSampleBytes, maxOutputBytes);
        InputSamples = BitConverter.ToInt32(inputSampleBytes, 0);
        MaxOutput = BitConverter.ToInt32(maxOutputBytes, 0);
        FrameSize = InputSamples * channels * sampleBit / 8;

        var ptr = FaacEncGetCurrentConfiguration(_handle);
        var configuration = InteropExtensions.IntPtrToStruct<FaacEncConfiguration>(ptr);
        configuration.inputFormat = 1;
        configuration.outputFormat = adts ? 1 : 0;
        configuration.useTns = 0;
        configuration.useLfe = 0;
        configuration.aacObjectType = 2;
        configuration.shortctl = 0;
        configuration.quantqual = 100;
        configuration.bandWidth = 0;
        configuration.bitRate = 0;
        InteropExtensions.IntPtrSetValue(ptr, configuration);

        if (FaacEncSetConfiguration(_handle, ptr) < 0) throw new Exception("set faac configuration failed!");
    }

    protected override void Dispose(Boolean disposing)
    {
        base.Dispose(disposing);

        if (_handle != IntPtr.Zero)
        {
            FaacEncClose(_handle);
            _handle = IntPtr.Zero;
        }
    }

    public Int32 Encode(Byte[] input, Byte[] output)
    {
        // 写入缓存数据尾部
        if (input != null && input.Length > 0)
        {
            var p = _cache.Position;
            _cache.Position = _cache.Length;
            _cache.Write(input);
            _cache.Position = p;
        }

        // faac必须达到一帧数据后才能正常编码
        var retain = _cache.Length - _cache.Position;
        if (retain < FrameSize) return 0;

        var buf = _cache.ReadBytes(FrameSize);
        if (buf == null || buf.Length != FrameSize) return 0;

        // 重用缓冲区
        if (_cache.Position == _cache.Length) _cache.SetLength(0);

        return FaacEncEncode(_handle, buf, InputSamples, output, output.Length);
    }

    public async Task<Int32> EncodeAsync(Stream inStream, Stream outStream)
    {
        var input = new Byte[FrameSize];
        var output = new Byte[MaxOutput];
        var rs = 0;

        while (true)
        {
            // 读取一帧数据
            var count = await inStream.ReadAsync(input, 0, input.Length);
            if (count == 0) break;

            var buf = input;
            if (count < input.Length) buf = input.ReadBytes(0, count);

            // 编码数据
            var len = FaacEncEncode(_handle, buf, InputSamples, output, output.Length);

            // 写入结果
            if (len > 0) await outStream.WriteAsync(output, 0, len);

            rs += len;
        }

        return rs;
    }

    private const String DLLFile = @"Audio/libfaac.dll";

    [DllImport(DLLFile, EntryPoint = "faacEncGetVersion")]
    //int FAACAPI faacEncGetVersion(char **faac_id_string, char **faac_copyright_string);
    private static extern Int32 FaacEncGetVersion(ref IntPtr faac_id_string, ref IntPtr faac_copyright_string);

    [DllImport(DLLFile, EntryPoint = "faacEncGetCurrentConfiguration", CallingConvention = CallingConvention.StdCall)]
    //faacEncConfigurationPtr FAACAPI faacEncGetCurrentConfiguration(faacEncHandle hEncoder);
    private static extern IntPtr FaacEncGetCurrentConfiguration(IntPtr hEncoder);

    [DllImport(DLLFile, EntryPoint = "faacEncSetConfiguration", CallingConvention = CallingConvention.StdCall)]
    //int FAACAPI faacEncSetConfiguration(faacEncHandle hEncoder,faacEncConfigurationPtr config);
    private static extern Int32 FaacEncSetConfiguration(IntPtr hEncoder, IntPtr config);

    [DllImport(DLLFile, EntryPoint = "faacEncOpen", CallingConvention = CallingConvention.StdCall)]
    //faacEncHandle FAACAPI faacEncOpen(unsigned long sampleRate, unsigned int numChannels, unsigned long *inputSamples, unsigned long *maxOutputBytes);
    private static extern IntPtr FaacEncOpen(Int32 sampleRate, Int32 numChannels, Byte[] inputSamples, Byte[] maxOutputBytes);

    [DllImport(DLLFile, EntryPoint = "faacEncGetDecoderSpecificInfo", CallingConvention = CallingConvention.StdCall)]
    //int FAACAPI faacEncGetDecoderSpecificInfo(faacEncHandle hEncoder, unsigned char **ppBuffer,unsigned long *pSizeOfDecoderSpecificInfo);
    private static extern IntPtr FaacEncGetDecoderSpecificInfo(IntPtr hEncoder, ref IntPtr ppBuffer, ref Int32 pSizeOfDecoderSpecificInfo);

    [DllImport(DLLFile, EntryPoint = "faacEncEncode", CallingConvention = CallingConvention.StdCall)]
    //int FAACAPI faacEncEncode(faacEncHandle hEncoder, int32_t * inputBuffer, unsigned int samplesInput, unsigned char *outputBuffer, unsigned int bufferSize);
    private static extern Int32 FaacEncEncode(IntPtr hEncoder, IntPtr inputBuffer, Int32 samplesInput, IntPtr outputBuffer, Int32 bufferSize);

    [DllImport(DLLFile, EntryPoint = "faacEncEncode", CallingConvention = CallingConvention.StdCall)]
    private static extern Int32 FaacEncEncode(IntPtr hEncoder, Byte[] inputBuffer, Int32 samplesInput, Byte[] outputBuffer, Int32 bufferSize);

    [DllImport(DLLFile, EntryPoint = "faacEncClose", CallingConvention = CallingConvention.StdCall)]
    //int FAACAPI faacEncClose(faacEncHandle hEncoder);
    private static extern IntPtr FaacEncClose(IntPtr hEncoder);

    #region 配置结构
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct FaacEncConfiguration
    {
        /* config version */

        public Int32 version;

        /* library version */
        public IntPtr name;

        /* copyright string */
        public IntPtr copyright;

        /* MPEG version, 2 or 4 */
        public UInt32 mpegVersion;

        /* AAC object type
         *  #define MAIN 1
            #define LOW  2
            #define SSR  3
            #define LTP  4
         * */

        public UInt32 aacObjectType;

        /* Allow mid/side coding */
        public UInt32 allowMidside;

        /* Use one of the channels as LFE channel */
        public UInt32 useLfe;

        /* Use Temporal Noise Shaping */
        public UInt32 useTns;

        /* bitrate / channel of AAC file */
        public UInt32 bitRate;

        /* AAC file frequency bandwidth */
        public UInt32 bandWidth;

        /* Quantizer quality */
        public UInt32 quantqual;

        /* Bitstream output format (0 = Raw; 1 = ADTS) */
        public Int32 outputFormat;

        /* psychoacoustic model list */
        public IntPtr psymodellist;

        /* selected index in psymodellist */
        public Int32 psymodelidx;

        /*
            PCM Sample Input Format
            0	FAAC_INPUT_NULL			invalid, signifies a misconfigured config
            1	FAAC_INPUT_16BIT		native endian 16bit
            2	FAAC_INPUT_24BIT		native endian 24bit in 24 bits		(not implemented)
            3	FAAC_INPUT_32BIT		native endian 24bit in 32 bits		(DEFAULT)
            4	FAAC_INPUT_FLOAT		32bit floating point
        */
        public Int32 inputFormat;

        /* block type enforcing (SHORTCTL_NORMAL/SHORTCTL_NOSHORT/SHORTCTL_NOLONG) */
        // #define FAAC_INPUT_NULL    0
        //#define FAAC_INPUT_16BIT   1
        //#define FAAC_INPUT_24BIT   2
        //#define FAAC_INPUT_32BIT   3
        //#define FAAC_INPUT_FLOAT   4

        //#define SHORTCTL_NORMAL    0
        //#define SHORTCTL_NOSHORT   1
        //#define SHORTCTL_NOLONG    2
        public Int32 shortctl;

        /*
            Channel Remapping

            Default			0, 1, 2, 3 ... 63  (64 is MAX_CHANNELS in coder.h)

            WAVE 4.0		2, 0, 1, 3
            WAVE 5.0		2, 0, 1, 3, 4
            WAVE 5.1		2, 0, 1, 4, 5, 3
            AIFF 5.1		2, 0, 3, 1, 4, 5 
        */
        [MarshalAs(UnmanagedType.ByValArray, ArraySubType = UnmanagedType.I4, SizeConst = 64)]
        public Int32[] channel_map;
    }
    #endregion
}

public static class InteropExtensions
{
    public static T BytesToStruct<T>(Byte[] bytes, Int32 startIndex, Int32 length)
    {
        T local;
        T local2;
        if (bytes == null)
        {
            local2 = default;
            local = local2;
        }
        else if (bytes.Length <= 0)
        {
            local2 = default;
            local = local2;
        }
        else
        {
            var destination = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.Copy(bytes, startIndex, destination, length);
                local = (T)Marshal.PtrToStructure(destination, typeof(T));
            }
            catch (Exception exception)
            {
                throw new Exception("Error in BytesToStruct ! " + exception.Message);
            }
            finally
            {
                Marshal.FreeHGlobal(destination);
            }
        }
        return local;
    }

    public static void IntPtrSetValue(IntPtr intptr, Object structObj) => IntPtrSetValue(intptr, StructToBytes(structObj));

    public static void IntPtrSetValue(IntPtr intptr, Byte[] bytes) => Marshal.Copy(bytes, 0, intptr, bytes.Length);

    public static T IntPtrToStruct<T>(IntPtr intptr)
    {
        var index = 0;
        return IntPtrToStruct<T>(intptr, index, Marshal.SizeOf(typeof(T)));
    }

    public static T IntPtrToStruct<T>(IntPtr intptr, Int32 index, Int32 length)
    {
        var destination = new Byte[length];
        Marshal.Copy(intptr, destination, index, length);
        return BytesToStruct<T>(destination, 0, destination.Length);
    }

    public static Byte[] StructToBytes(Object structObj)
    {
        var size = Marshal.SizeOf(structObj);
        return StructToBytes(structObj, size);
    }

    public static Byte[] StructToBytes(Object structObj, Int32 size)
    {
        Byte[] buffer2;
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(structObj, ptr, false);
            var destination = new Byte[size];
            Marshal.Copy(ptr, destination, 0, size);
            buffer2 = destination;
        }
        catch (Exception exception)
        {
            throw new Exception("Error in StructToBytes ! " + exception.Message);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
        return buffer2;
    }
}