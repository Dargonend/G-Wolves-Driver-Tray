// ============================================================================
//  GWMouseBattery - 核心层
//  G-Wolves 无线鼠标电量读取（原生 HID，不依赖浏览器 / WebHID / 官方驱动）
//
//  协议来源：mouse.xyz / mouse.pink 网页驱动的公开 JS bundle，已有公开整理：
//    * compx 家族：17 字节中断报文，报告 ID 8，命令 0x04
//    * feature 家族：65 字节 feature 报文，命令 0x83         <- 其它 G-Wolves 型号
//
//  重要安全约定：本文件只发送两个"读"命令（0x04 / 0x83）。
//  该协议里还存在破坏性命令（如 0xB0 进入 bootloader），绝对不可试探性乱发。
//
//  本文件必须保存为 UTF-8 with BOM，否则 csc 会按 ANSI 误读中文。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace GWMouseBattery
{
    #region Win32

    internal static class Native
    {
        public const uint GENERIC_READ = 0x80000000;
        public const uint GENERIC_WRITE = 0x40000000;
        public const uint FILE_SHARE_READ = 1;
        public const uint FILE_SHARE_WRITE = 2;
        public const uint OPEN_EXISTING = 3;
        public const uint FILE_FLAG_OVERLAPPED = 0x40000000;

        public const int DIGCF_PRESENT = 0x02;
        public const int DIGCF_DEVICEINTERFACE = 0x10;

        public const int ERROR_IO_PENDING = 997;
        public const int ERROR_INSUFFICIENT_BUFFER = 122;

        public const uint WAIT_OBJECT_0 = 0;
        public const uint WAIT_TIMEOUT = 258;

        public static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVINFO_DATA
        {
            public int cbSize;
            public Guid ClassGuid;
            public int DevInst;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDD_ATTRIBUTES
        {
            public int Size;
            public ushort VendorID;
            public ushort ProductID;
            public ushort VersionNumber;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDP_CAPS
        {
            public ushort Usage;
            public ushort UsagePage;
            public ushort InputReportByteLength;
            public ushort OutputReportByteLength;
            public ushort FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
            public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes;
            public ushort NumberInputButtonCaps;
            public ushort NumberInputValueCaps;
            public ushort NumberInputDataIndices;
            public ushort NumberOutputButtonCaps;
            public ushort NumberOutputValueCaps;
            public ushort NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps;
            public ushort NumberFeatureValueCaps;
            public ushort NumberFeatureDataIndices;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct OVERLAPPED
        {
            public IntPtr Internal;
            public IntPtr InternalHigh;
            public int Offset;
            public int OffsetHigh;
            public IntPtr hEvent;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sa,
            uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr h);

        // 注意：重叠 I/O 的缓冲区与 OVERLAPPED 一律用非托管内存传指针。
        // 用 byte[] / ref 结构体的话，P/Invoke 只在调用期间固定地址，
        // 返回 ERROR_IO_PENDING 后 GC 一旦压缩堆，内核写回的地址就失效了。
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ReadFile(IntPtr h, IntPtr buffer, int toRead, out int read, IntPtr ov);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool WriteFile(IntPtr h, IntPtr buffer, int toWrite, out int written, IntPtr ov);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr CreateEventW(IntPtr sa, bool manualReset, bool initialState, IntPtr name);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ResetEvent(IntPtr h);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WaitForSingleObject(IntPtr h, uint ms);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetOverlappedResult(IntPtr h, IntPtr ov, out int transferred, bool wait);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CancelIo(IntPtr h);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator,
            IntPtr hwndParent, int flags);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid classGuid,
            int index, ref SP_DEVICE_INTERFACE_DATA data);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data,
            IntPtr detail, int detailSize, out int required, ref SP_DEVINFO_DATA devInfo);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr set, ref SP_DEVINFO_DATA devInfo,
            int property, IntPtr propertyRegDataType, byte[] buffer, int bufferSize, out int requiredSize);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        [DllImport("hid.dll")]
        public static extern void HidD_GetHidGuid(out Guid hidGuid);

        [DllImport("hid.dll")]
        public static extern bool HidD_GetAttributes(IntPtr h, ref HIDD_ATTRIBUTES attributes);

        [DllImport("hid.dll")]
        public static extern bool HidD_GetPreparsedData(IntPtr h, out IntPtr preparsed);

        [DllImport("hid.dll")]
        public static extern bool HidD_FreePreparsedData(IntPtr preparsed);

        [DllImport("hid.dll")]
        public static extern int HidP_GetCaps(IntPtr preparsed, ref HIDP_CAPS caps);

        [DllImport("hid.dll")]
        public static extern bool HidD_SetFeature(IntPtr h, byte[] buffer, int length);

        [DllImport("hid.dll")]
        public static extern bool HidD_GetFeature(IntPtr h, byte[] buffer, int length);

        // ---- 单实例互斥：用窗口消息把"再点一次"变成"把窗口叫出来" ----
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr FindWindowW(string className, string windowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern uint RegisterWindowMessageW(string message);

        public const int HIDP_STATUS_SUCCESS = 0x00110000;
        public const int SPDRP_DEVICEDESC = 0x00;
        public const int SPDRP_FRIENDLYNAME = 0x0C;
    }

    #endregion

    #region 设备描述

    internal enum ProtocolKind
    {
        Unknown = 0,
        Compx = 1,        // 17 字节中断报文，报告 ID 8
        Feature = 2       // 65 字节 feature 报文
    }

    internal sealed class HidInterface
    {
        public string Path = "";
        public string FriendlyName = "";
        public ushort VendorId;
        public ushort ProductId;
        public ushort UsagePage;
        public ushort Usage;
        public int InputLength;
        public int OutputLength;
        public int FeatureLength;
        public ProtocolKind Kind = ProtocolKind.Unknown;

        public string VidPidHex
        {
            get { return "0x" + VendorId.ToString("X4") + ":0x" + ProductId.ToString("X4"); }
        }

        public string KindName
        {
            get
            {
                if (Kind == ProtocolKind.Compx) return "compx（17 字节中断报文）";
                if (Kind == ProtocolKind.Feature) return "feature（65 字节 feature 报文）";
                return "未知";
            }
        }
    }

    internal sealed class BatteryReading
    {
        public bool Ok;
        public int Percent = -1;
        public bool Charging;
        public int VoltageMv = -1;
        public string Error = "";
        public string Protocol = "";
        public string Path = "";
        public byte[] Raw;
        public DateTime Stamp = DateTime.Now;

        public string RawHex
        {
            get
            {
                if (Raw == null) return "";
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < Raw.Length; i++)
                {
                    if (i > 0) sb.Append(' ');
                    sb.Append(Raw[i].ToString("X2"));
                }
                return sb.ToString();
            }
        }
    }

    #endregion

    #region 协议

    /// <summary>
    /// compx 家族：报告 ID 8，输入/输出报文各 17 字节（1 字节报告 ID + 16 字节载荷）。
    /// 回复以"输入报文"形式到达，而不是读命令的应答，所以必须先挂起读再写。
    /// </summary>
    internal static class CompxProtocol
    {
        public const byte ReportId = 8;
        public const int ReportLength = 17;
        public const int PayloadSize = 16;
        public const byte CmdBattery = 0x04;

        // ---- EEPROM 读写：SPDT 之类的设置就存在 EEPROM 里 ----
        public const byte CmdWriteEeprom = 0x07;
        public const byte CmdReadEeprom = 0x08;

        // ---- 配置档（用来"重新加载"，让刚写进去的设置生效）----
        public const byte CmdGetCurrentConfig = 0x0E;
        public const byte CmdSetCurrentConfig = 0x0F;

        public static byte[] BuildGetCurrentConfig()
        {
            byte[] payload = new byte[PayloadSize];
            payload[0] = CmdGetCurrentConfig;
            payload[PayloadSize - 1] = Checksum(payload);
            return Wrap(payload);
        }

        /// <summary>切到指定配置档（1 起算）。传当前档就是"重新加载一遍"。</summary>
        public static byte[] BuildSetCurrentConfig(int profile1Based)
        {
            byte[] payload = new byte[PayloadSize];
            payload[0] = CmdSetCurrentConfig;
            payload[4] = 1;
            payload[5] = (byte)((profile1Based - 1) & 0xFF);
            payload[PayloadSize - 1] = Checksum(payload);
            return Wrap(payload);
        }

        /// <summary>SPDT 状态所在的 EEPROM 地址（驱动里的 Ge.KeyOperation）。</summary>
        public const int KeyOperationAddress = 8;
        public const int SpdtLeftBit = 1;
        public const int SpdtRightBit = 2;

        // ---- 其它 EEPROM 地址（都取自驱动里的 Ge 表）----
        public const int ReportRateAddress = 0;      // 回报率
        public const int MaxDpiStageAddress = 2;     // 档位数
        public const int CurrentDpiAddress = 4;      // 当前档位
        public const int LodAddress = 10;            // 静默高度
        public const int DpiValueAddress = 12;       // 3950 传感器：每档 4 字节
        public const int Sensor3955DpiAddress = 6912; // 3955 传感器：每档 6 字节

        public static bool SpdtLeft(int keyOperationLowByte)
        {
            return (keyOperationLowByte & SpdtLeftBit) == SpdtLeftBit;
        }

        public static bool SpdtRight(int keyOperationLowByte)
        {
            return (keyOperationLowByte & SpdtRightBit) == SpdtRightBit;
        }

        /// <summary>读 EEPROM：payload[0]=8, [2..3]=地址(大端), [4]=长度。</summary>
        public static byte[] BuildEepromRead(int address, int length)
        {
            byte[] payload = new byte[PayloadSize];
            payload[0] = CmdReadEeprom;
            payload[1] = 0;
            payload[2] = (byte)((address >> 8) & 0xFF);
            payload[3] = (byte)(address & 0xFF);
            payload[4] = (byte)(length & 0xFF);
            payload[PayloadSize - 1] = Checksum(payload);
            return Wrap(payload);
        }

        /// <summary>
        /// 写 EEPROM：payload[0]=7, [2..3]=地址, [4]=长度, [5]=值, [6]=85-值。
        /// 最后那个"85-值"是驱动里固定跟着发的校验字节，必须一起发。
        /// </summary>
        public static byte[] BuildEepromWrite(int address, int value, int length)
        {
            byte[] payload = new byte[PayloadSize];
            payload[0] = CmdWriteEeprom;
            payload[1] = 0;
            payload[2] = (byte)((address >> 8) & 0xFF);
            payload[3] = (byte)(address & 0xFF);
            payload[4] = (byte)(length & 0xFF);
            payload[5] = (byte)(value & 0xFF);
            payload[6] = (byte)((85 - (value & 0xFF)) & 0xFF);
            payload[PayloadSize - 1] = Checksum(payload);
            return Wrap(payload);
        }

        private static byte[] Wrap(byte[] payload)
        {
            byte[] frame = new byte[ReportLength];
            frame[0] = ReportId;
            Array.Copy(payload, 0, frame, 1, PayloadSize);
            return frame;
        }

        /// <summary>
        /// 写一段 EEPROM 数据（驱动里的 Set_Device_Eeprom_Array），一次最多 10 字节。
        /// 与单值写入的区别：数据直接放在 payload[5..]，不带 "85-值" 那个伴随字节。
        /// </summary>
        public static byte[] BuildEepromWriteArray(int address, byte[] data)
        {
            if (data == null) data = new byte[0];
            if (data.Length > 10) throw new ArgumentException("一次最多写 10 字节");

            byte[] payload = new byte[PayloadSize];
            payload[0] = CmdWriteEeprom;
            payload[1] = 0;
            payload[2] = (byte)((address >> 8) & 0xFF);
            payload[3] = (byte)(address & 0xFF);
            payload[4] = (byte)data.Length;
            for (int i = 0; i < data.Length; i++) payload[5 + i] = data[i];
            payload[PayloadSize - 1] = Checksum(payload);
            return Wrap(payload);
        }

        /// <summary>
        /// 载荷最后一字节的校验和。驱动里是 `85 - sum(payload[0..14])`，
        /// 再减去报告 ID，最后截断成 8 位。
        /// </summary>
        public static byte Checksum(byte[] payload)
        {
            int sum = 0;
            for (int i = 0; i < PayloadSize - 1; i++) sum += payload[i];
            return (byte)((85 - (sum & 0xFF) - ReportId) & 0xFF);
        }

        /// <summary>构造一整帧（含报告 ID）。</summary>
        public static byte[] BuildRequest(byte command)
        {
            byte[] payload = new byte[PayloadSize];
            payload[0] = command;
            payload[PayloadSize - 1] = Checksum(payload);
            return Wrap(payload);
        }

        public static byte[] BuildBatteryRequest()
        {
            return BuildRequest(CmdBattery);
        }

        /// <summary>解析回复。raw 的第 0 字节是报告 ID。</summary>
        public static bool TryParse(byte[] raw, out int percent, out bool charging, out int voltageMv)
        {
            percent = -1;
            charging = false;
            voltageMv = -1;

            if (raw == null || raw.Length < 10) return false;
            if (raw[1] != CmdBattery) return false;

            int level = raw[6];
            if (level < 0 || level > 100) return false;

            percent = level;
            charging = raw[7] != 0;
            voltageMv = (raw[8] << 8) | raw[9];
            return true;
        }
    }

    /// <summary>
    /// feature 家族：65 字节 feature 报文（报告 ID 0 + 64 字节载荷），命令 0x83。
    /// 用于其它采用同一套驱动协议的 G-Wolves 型号。
    /// </summary>
    internal static class FeatureProtocol
    {
        public const int PayloadSize = 64;
        public const int BufferLength = 65;
        public const byte CmdBattery = 0x83;
        public const byte ResponseHeader = 0xA1;
        public const byte DefaultDeviceId = 2;

        public static byte[] BuildBatteryRequest(byte deviceId, int bufferLength)
        {
            int len = bufferLength >= BufferLength ? bufferLength : BufferLength;
            byte[] buf = new byte[len];
            buf[0] = 0;                       // 报告 ID
            buf[1 + 2] = deviceId;            // payload[2]
            buf[1 + 3] = 2;                   // payload[3] 常量
            buf[1 + 5] = CmdBattery;          // payload[5]
            return buf;
        }

        public static bool TryParse(byte[] raw, out int percent, out bool charging)
        {
            percent = -1;
            charging = false;

            if (raw == null || raw.Length < 9) return false;
            if (raw[1] != ResponseHeader) return false;
            if (raw[6] != CmdBattery) return false;

            int level = raw[8];
            if (level < 0 || level > 100) return false;

            percent = level;
            charging = raw[7] != 0;
            return true;
        }
    }

    #endregion

    #region HID 枚举与查找

    internal static class HidDiscovery
    {
        /// <summary>把所有 HID 接口列出来，并给每个接口判定它属于哪个协议家族。</summary>
        public static List<HidInterface> Enumerate(out string error)
        {
            error = "";
            List<HidInterface> list = new List<HidInterface>();

            Guid hidGuid;
            Native.HidD_GetHidGuid(out hidGuid);

            IntPtr set = Native.SetupDiGetClassDevsW(ref hidGuid, IntPtr.Zero, IntPtr.Zero,
                Native.DIGCF_PRESENT | Native.DIGCF_DEVICEINTERFACE);
            if (set == Native.INVALID_HANDLE_VALUE)
            {
                error = "SetupDiGetClassDevs 失败，错误码 " + Marshal.GetLastWin32Error();
                return list;
            }

            try
            {
                Native.SP_DEVICE_INTERFACE_DATA data = new Native.SP_DEVICE_INTERFACE_DATA();
                data.cbSize = Marshal.SizeOf(typeof(Native.SP_DEVICE_INTERFACE_DATA));

                for (int index = 0; ; index++)
                {
                    if (!Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, index, ref data))
                        break;

                    Native.SP_DEVINFO_DATA devInfo = new Native.SP_DEVINFO_DATA();
                    devInfo.cbSize = Marshal.SizeOf(typeof(Native.SP_DEVINFO_DATA));

                    string rawPath = GetInterfacePath(set, ref data, ref devInfo);
                    if (string.IsNullOrEmpty(rawPath)) continue;

                    HidInterface item = Inspect(rawPath);
                    if (item == null) continue;

                    item.FriendlyName = ReadFriendlyName(set, ref devInfo);
                    list.Add(item);
                }
            }
            finally
            {
                Native.SetupDiDestroyDeviceInfoList(set);
            }

            return list;
        }

        /// <summary>
        /// 打开接口读属性/能力，并判定协议家族。
        /// 判定依据完全来自 HID 描述符，不需要发任何命令：
        ///   compx   -> 输入与输出报文都是 17 字节
        ///   feature -> 存在 65 字节的 feature 报文
        /// </summary>
        private static HidInterface Inspect(string rawPath)
        {
            string path = Util.NormalizeDevicePath(rawPath);

            IntPtr h = Native.CreateFileW(path, Native.GENERIC_READ | Native.GENERIC_WRITE,
                Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero,
                Native.OPEN_EXISTING, Native.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (h == Native.INVALID_HANDLE_VALUE)
            {
                // 有些接口不给写权限，退化成只读打开，至少还能读到描述符
                h = Native.CreateFileW(path, 0, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
                    IntPtr.Zero, Native.OPEN_EXISTING, Native.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            }
            if (h == Native.INVALID_HANDLE_VALUE) return null;

            try
            {
                HidInterface item = new HidInterface();
                item.Path = path;

                Native.HIDD_ATTRIBUTES attr = new Native.HIDD_ATTRIBUTES();
                attr.Size = Marshal.SizeOf(typeof(Native.HIDD_ATTRIBUTES));
                if (Native.HidD_GetAttributes(h, ref attr))
                {
                    item.VendorId = attr.VendorID;
                    item.ProductId = attr.ProductID;
                }

                IntPtr preparsed;
                if (!Native.HidD_GetPreparsedData(h, out preparsed)) return null;
                try
                {
                    Native.HIDP_CAPS caps = new Native.HIDP_CAPS();
                    if (Native.HidP_GetCaps(preparsed, ref caps) != Native.HIDP_STATUS_SUCCESS)
                        return null;

                    item.Usage = caps.Usage;
                    item.UsagePage = caps.UsagePage;
                    item.InputLength = caps.InputReportByteLength;
                    item.OutputLength = caps.OutputReportByteLength;
                    item.FeatureLength = caps.FeatureReportByteLength;
                }
                finally
                {
                    Native.HidD_FreePreparsedData(preparsed);
                }

                item.Kind = Classify(item);
                return item;
            }
            finally
            {
                Native.CloseHandle(h);
            }
        }

        public static ProtocolKind Classify(HidInterface item)
        {
            if (item.InputLength == CompxProtocol.ReportLength
                && item.OutputLength == CompxProtocol.ReportLength)
                return ProtocolKind.Compx;

            if (item.FeatureLength >= FeatureProtocol.BufferLength)
                return ProtocolKind.Feature;

            return ProtocolKind.Unknown;
        }

        private static string GetInterfacePath(IntPtr set, ref Native.SP_DEVICE_INTERFACE_DATA data,
            ref Native.SP_DEVINFO_DATA devInfo)
        {
            int required;
            // 传 NULL 缓冲区只是询问所需大小，按设计必然返回 FALSE，不能当失败判据
            Native.SetupDiGetDeviceInterfaceDetailW(set, ref data, IntPtr.Zero, 0, out required, ref devInfo);
            if (required <= 0) return null;

            IntPtr buffer = Marshal.AllocHGlobal(required);
            try
            {
                // SP_DEVICE_INTERFACE_DETAIL_DATA 的 cbSize 只有 4(32位)/8(64位)
                Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 4);
                devInfo.cbSize = Marshal.SizeOf(typeof(Native.SP_DEVINFO_DATA));

                if (!Native.SetupDiGetDeviceInterfaceDetailW(set, ref data, buffer, required,
                        out required, ref devInfo))
                    return null;

                IntPtr text = IntPtr.Size == 8
                    ? (IntPtr)(buffer.ToInt64() + 8)
                    : (IntPtr)(buffer.ToInt32() + 4);
                return Marshal.PtrToStringUni(text);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static string ReadFriendlyName(IntPtr set, ref Native.SP_DEVINFO_DATA devInfo)
        {
            try
            {
                byte[] buf = new byte[1024];
                int required;
                if (!Native.SetupDiGetDeviceRegistryPropertyW(set, ref devInfo, Native.SPDRP_FRIENDLYNAME,
                        IntPtr.Zero, buf, buf.Length, out required))
                {
                    if (!Native.SetupDiGetDeviceRegistryPropertyW(set, ref devInfo, Native.SPDRP_DEVICEDESC,
                            IntPtr.Zero, buf, buf.Length, out required))
                        return "";
                }
                if (required <= 0) return "";
                if (required > buf.Length) required = buf.Length;
                string s = Encoding.Unicode.GetString(buf, 0, required);
                int nul = s.IndexOf('\0');
                return nul >= 0 ? s.Substring(0, nul) : s;
            }
            catch
            {
                return "";
            }
        }

        /// <summary>
        /// 找可用的设备。只认 G-Wolves 自家的 VID —— 协议帧格式是厂商私有的，
        /// 别的品牌即使 HID 描述符长得一样也读不到电量，所以不做跨品牌尝试。
        /// </summary>
        public static HidInterface Find(out List<HidInterface> all, out string error)
        {
            all = Enumerate(out error);
            if (all.Count == 0) return null;

            // 只在自己 VID 里找；同类里 compx 更常见，优先
            HidInterface best = null;
            int bestScore = int.MaxValue;

            foreach (HidInterface item in all)
            {
                if (item.Kind == ProtocolKind.Unknown) continue;

                if (item.VendorId != ProductIds.GWolvesVendorId) continue;

                int score = 0;
                if (item.Kind == ProtocolKind.Feature) score += 1;   // compx 更常见，优先

                if (score < bestScore)
                {
                    bestScore = score;
                    best = item;
                }
            }

            return best;
        }
    }

    internal static class ProductIds
    {
        public const ushort GWolvesVendorId = 0x33E4;
    }

    #endregion

    #region 电量查询

    internal static class BatteryClient
    {
        /// <summary>查询一次电量。整个过程有超时上限，不会无限期挂住。</summary>
        public static BatteryReading Read(HidInterface item)
        {
            BatteryReading r = new BatteryReading();
            if (item == null)
            {
                r.Error = "没有找到可用的设备接口";
                return r;
            }

            r.Path = item.Path;
            r.Protocol = item.KindName;

            try
            {
                if (item.Kind == ProtocolKind.Compx) ReadCompx(item, r);
                else if (item.Kind == ProtocolKind.Feature) ReadFeature(item, r);
                else r.Error = "该接口的协议家族未知";
            }
            catch (Exception ex)
            {
                r.Error = ex.GetType().Name + ": " + ex.Message;
            }

            return r;
        }

        /// <summary>
        /// compx 通用收发：挂起读 -> 写 17 字节帧 -> 等一个 reply[1] == 命令字 的输入报文。
        ///
        /// 【关键】等回复时必须**耐心等同一个已经挂起的读**，绝不能"超时了就 CancelIo
        /// 再重新挂起"：取消到重挂之间有一小段空窗，回复正好落在空窗里就永久丢了；
        /// 而设备省电时回复本来就要等上百毫秒。同一台设备实测：
        /// 耐心等 = 93% 成功率，每 120ms 取消重挂 = 只有 20% 左右。
        /// </summary>
        public static bool ExchangeCompx(HidInterface item, byte[] request, out byte[] reply, out string error)
        {
            reply = null;
            error = "";

            IntPtr h = Native.CreateFileW(item.Path,
                Native.GENERIC_READ | Native.GENERIC_WRITE,
                Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero,
                Native.OPEN_EXISTING, Native.FILE_FLAG_OVERLAPPED, IntPtr.Zero);

            if (h == Native.INVALID_HANDLE_VALUE)
            {
                error = "无法打开设备（错误 " + Marshal.GetLastWin32Error() + "）";
                return false;
            }

            try
            {
                int outLen = item.OutputLength > 0 ? item.OutputLength : CompxProtocol.ReportLength;
                int inLen = item.InputLength > 0 ? item.InputLength : CompxProtocol.ReportLength;
                if (outLen < CompxProtocol.ReportLength) outLen = CompxProtocol.ReportLength;
                if (inLen < CompxProtocol.ReportLength) inLen = CompxProtocol.ReportLength;

                byte command = request.Length > 1 ? request[1] : (byte)0;

                for (int attempt = 0; attempt < 3; attempt++)
                {
                    PendingIo read = null;
                    PendingIo write = null;
                    try
                    {
                        // ① 先把读挂起 —— 设备可能在 write 返回之前就回答了
                        read = PendingIo.ArmRead(h, inLen);
                        if (read == null) break;

                        // ② 写命令
                        write = PendingIo.StartWrite(h, request, outLen);
                        if (write != null) write.Wait(800);

                        // ③ 耐心等这一个已经挂起的读（原因见方法注释）
                        int deadline = Environment.TickCount + 1200;
                        while (Environment.TickCount < deadline)
                        {
                            int remain = deadline - Environment.TickCount;
                            if (remain <= 0) break;

                            int slice = remain < 250 ? remain : 250;
                            int got = read.Wait(slice);

                            if (got <= 0) continue;          // 还没来，继续等同一个读

                            if (got >= 2 && read.Buffer[1] == command)
                            {
                                reply = new byte[got];
                                Array.Copy(read.Buffer, reply, got);
                                return true;
                            }

                            // 收到的是无关报文：重新挂起，把剩下的时间等完
                            read.Dispose();
                            read = PendingIo.ArmRead(h, inLen);
                            if (read == null) break;
                        }
                    }
                    finally
                    {
                        if (write != null) write.Dispose();
                        if (read != null) read.Dispose();
                    }

                    Thread.Sleep(80);
                }

                error = "设备没有回复电量报文（可能鼠标已休眠或关机，或网页驱动正占用设备）";
                return false;
            }
            finally
            {
                Native.CloseHandle(h);
            }
        }

        /// <summary>compx 家族读一次电量。</summary>
        private static void ReadCompx(HidInterface item, BatteryReading r)
        {
            byte[] reply;
            string error;

            if (!ExchangeCompx(item, CompxProtocol.BuildBatteryRequest(), out reply, out error))
            {
                r.Error = error;
                return;
            }

            r.Raw = reply;

            int percent, voltage;
            bool charging;
            if (CompxProtocol.TryParse(reply, out percent, out charging, out voltage))
            {
                r.Ok = true;
                r.Percent = percent;
                r.Charging = charging;
                r.VoltageMv = voltage;
                r.Stamp = DateTime.Now;
                return;
            }

            r.Error = "收到了回复但内容不是电量格式：" + Util.Hex(reply, reply.Length);
        }

        /// <summary>
        /// [诊断] 监听 compx 接口若干秒，把收到的**每一帧**都打出来 —— 包括别的程序
        /// （官方驱动 / 浏览器 WebHID）触发的回复。收到 echo 不是 0x04 的帧，就说明
        /// 有别的程序在同时跟这只设备说话。
        /// </summary>
        public static void ListenCompx(HidInterface item, int seconds, Action<string> output)
        {
            IntPtr h = Native.CreateFileW(item.Path,
                Native.GENERIC_READ | Native.GENERIC_WRITE,
                Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero,
                Native.OPEN_EXISTING, Native.FILE_FLAG_OVERLAPPED, IntPtr.Zero);

            if (h == Native.INVALID_HANDLE_VALUE)
            {
                output("无法打开设备（错误 " + Marshal.GetLastWin32Error() + "）");
                return;
            }

            try
            {
                int inLen = item.InputLength > 0 ? item.InputLength : CompxProtocol.ReportLength;
                int outLen = item.OutputLength > 0 ? item.OutputLength : CompxProtocol.ReportLength;
                if (inLen < CompxProtocol.ReportLength) inLen = CompxProtocol.ReportLength;
                if (outLen < CompxProtocol.ReportLength) outLen = CompxProtocol.ReportLength;

                byte[] request = CompxProtocol.BuildBatteryRequest();
                DateTime end = DateTime.Now.AddSeconds(seconds);
                DateTime nextWrite = DateTime.Now;

                PendingIo read = null;
                DateTime armedAt = DateTime.Now;

                while (DateTime.Now < end)
                {
                    if (read == null)
                    {
                        read = PendingIo.ArmRead(h, inLen);
                        armedAt = DateTime.Now;
                        if (read == null) { output("挂起读失败"); break; }
                    }

                    if (DateTime.Now >= nextWrite)
                    {
                        PendingIo write = PendingIo.StartWrite(h, request, outLen);
                        if (write != null)
                        {
                            write.Wait(500);
                            write.Dispose();
                            output(DateTime.Now.ToString("HH:mm:ss.fff") + "  -> 写电量命令 08 04 .. 49");
                        }
                        nextWrite = DateTime.Now.AddSeconds(1);
                    }

                    int got = read.Wait(150);

                    if (got > 0)
                    {
                        byte echo = got > 1 ? read.Buffer[1] : (byte)0;
                        string tag = echo == CompxProtocol.CmdBattery
                            ? "  <= 电量回复"
                            : "  <= 其它命令的回复（说明另有程序在跟设备说话）";
                        output(DateTime.Now.ToString("HH:mm:ss.fff") + "  <- " + HexOf(read.Buffer, got) + tag);

                        read.Dispose();
                        read = null;
                    }
                    else if ((DateTime.Now - armedAt).TotalSeconds > 3)
                    {
                        output(DateTime.Now.ToString("HH:mm:ss.fff") + "  .. 3 秒内没有收到任何帧，重新挂起读");
                        read.Dispose();
                        read = null;
                    }
                }

                if (read != null) read.Dispose();
            }
            finally
            {
                Native.CloseHandle(h);
            }
        }

        /// <summary>compx 命令号 → 人话（用来解读别人发过来的回复的 echo 字节）。</summary>
        public static string CommandName(byte command)
        {
            switch (command)
            {
                case 0x01: return "取 EID / 接收器 PR";
                case 0x04: return "读电量";
                case 0x05: return "进入配对模式";
                case 0x06: return "取配对结果";
                case 0x07: return "写 EEPROM";
                case 0x08: return "读 EEPROM";
                case 0x09: return "恢复默认设置";
                case 0x0E: return "读当前配置档";
                case 0x0F: return "切换配置档";
                case 0x12: return "取固件版本";
                case 0x16: return "写长距离模式";
                case 0x17: return "读长距离模式";
                case 0x1D: return "取固件版本（带设备号）";
                case 0x2E: return "开始校准";
                default: return "未知命令";
            }
        }

        /// <summary>
        /// [诊断] **只监听、不发任何命令**，把设备上收到的每一帧打出来。
        /// 每帧的 raw[1] 是"发送方那条命令的 echo"，所以别的程序（官方驱动 / 浏览器）
        /// 一旦跟设备说话，这里就能看到它用的是哪条命令、按什么顺序发的。
        /// </summary>
        public static void SniffCompx(HidInterface item, int seconds, Action<string> output)
        {
            IntPtr h = Native.CreateFileW(item.Path,
                Native.GENERIC_READ | Native.GENERIC_WRITE,
                Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero,
                Native.OPEN_EXISTING, Native.FILE_FLAG_OVERLAPPED, IntPtr.Zero);

            if (h == Native.INVALID_HANDLE_VALUE)
            {
                output("无法打开设备（错误 " + Marshal.GetLastWin32Error() + "）");
                return;
            }

            try
            {
                int inLen = item.InputLength > 0 ? item.InputLength : CompxProtocol.ReportLength;
                if (inLen < CompxProtocol.ReportLength) inLen = CompxProtocol.ReportLength;

                DateTime end = DateTime.Now.AddSeconds(seconds);
                PendingIo read = null;
                DateTime armedAt = DateTime.Now;
                int frames = 0;

                while (DateTime.Now < end)
                {
                    if (read == null)
                    {
                        read = PendingIo.ArmRead(h, inLen);
                        armedAt = DateTime.Now;
                        if (read == null) { output("挂起读失败"); break; }
                    }

                    int got = read.Wait(200);

                    if (got > 0)
                    {
                        frames++;
                        byte echo = got > 1 ? read.Buffer[1] : (byte)0;
                        output(DateTime.Now.ToString("HH:mm:ss.fff") + "  <- " + HexOf(read.Buffer, got)
                            + "   [echo 0x" + echo.ToString("X2") + " = " + CommandName(echo) + "]");

                        read.Dispose();
                        read = null;
                    }
                    else if ((DateTime.Now - armedAt).TotalSeconds > 4)
                    {
                        read.Dispose();
                        read = null;
                    }
                }

                if (read != null) read.Dispose();
                output("");
                output("监听 " + seconds + " 秒，共收到 " + frames + " 帧。");
            }
            finally
            {
                Native.CloseHandle(h);
            }
        }

        private static string HexOf(byte[] buffer, int length)
        {
            if (length > buffer.Length) length = buffer.Length;
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < length; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(buffer[i].ToString("X2"));
            }
            return sb.ToString();
        }

        /// <summary>feature：HidD_SetFeature 写命令，等 100ms 后 HidD_GetFeature 读回复。</summary>
        private static void ReadFeature(HidInterface item, BatteryReading r)
        {
            IntPtr h = Native.CreateFileW(item.Path,
                Native.GENERIC_READ | Native.GENERIC_WRITE,
                Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero,
                Native.OPEN_EXISTING, 0, IntPtr.Zero);

            if (h == Native.INVALID_HANDLE_VALUE)
            {
                r.Error = "无法打开设备（错误 " + Marshal.GetLastWin32Error() + "）";
                return;
            }

            try
            {
                int len = item.FeatureLength >= FeatureProtocol.BufferLength
                    ? item.FeatureLength : FeatureProtocol.BufferLength;

                byte[] request = FeatureProtocol.BuildBatteryRequest(
                    FeatureProtocol.DefaultDeviceId, len);

                for (int attempt = 0; attempt < 3; attempt++)
                {
                    if (Native.HidD_SetFeature(h, request, request.Length))
                    {
                        Thread.Sleep(100);

                        byte[] response = new byte[len];
                        if (Native.HidD_GetFeature(h, response, response.Length))
                        {
                            r.Raw = response;

                            int percent;
                            bool charging;
                            if (FeatureProtocol.TryParse(response, out percent, out charging))
                            {
                                r.Ok = true;
                                r.Percent = percent;
                                r.Charging = charging;
                                r.Stamp = DateTime.Now;
                                return;
                            }
                        }
                    }

                    Thread.Sleep(60);
                }

                r.Error = "设备没有回复电量报文";
            }
            finally
            {
                Native.CloseHandle(h);
            }
        }

        /// <summary>
        /// 把一次"挂起中的重叠 I/O"打包起来。
        ///
        /// 缓冲区与 OVERLAPPED 都放在非托管内存：I/O 挂起期间内核会直接往里写，
        /// 托管堆上的数组/结构体可能被 GC 压缩搬走，那样内核就写到了野地址。
        /// 用完必须 Dispose（会取消未完成的 I/O 并等它真正结束再释放内存）。
        /// </summary>
        private sealed class PendingIo
        {
            public byte[] Buffer;          // 给调用方看的托管副本

            private IntPtr _handle;
            private IntPtr _raw;           // 非托管数据缓冲区
            private IntPtr _ov;            // 非托管 OVERLAPPED
            private IntPtr _event;
            private int _length;
            private bool _pending;
            private bool _alive;

            public static PendingIo ArmRead(IntPtr handle, int length)
            {
                PendingIo io = new PendingIo();
                if (!io.Start(handle, true, length, null)) return null;
                return io;
            }

            public static PendingIo StartWrite(IntPtr handle, byte[] data, int length)
            {
                PendingIo io = new PendingIo();
                if (!io.Start(handle, false, length, data)) return null;
                return io;
            }

            private bool Start(IntPtr handle, bool isRead, int length, byte[] payload)
            {
                _handle = handle;
                _length = length;
                Buffer = new byte[length];
                _raw = Marshal.AllocHGlobal(length);
                _ov = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Native.OVERLAPPED)));
                _event = Native.CreateEventW(IntPtr.Zero, true, false, IntPtr.Zero);

                if (_raw == IntPtr.Zero || _ov == IntPtr.Zero || _event == IntPtr.Zero)
                {
                    Dispose();
                    return false;
                }

                if (payload != null)
                {
                    int n = Math.Min(payload.Length, length);
                    Marshal.Copy(payload, 0, _raw, n);
                }

                Native.OVERLAPPED ov = new Native.OVERLAPPED();
                ov.hEvent = _event;
                Marshal.StructureToPtr(ov, _ov, false);

                _alive = true;

                int transferred;
                bool ok = isRead
                    ? Native.ReadFile(handle, _raw, length, out transferred, _ov)
                    : Native.WriteFile(handle, _raw, length, out transferred, _ov);

                if (ok)
                {
                    _pending = false;     // 立即完成
                    return true;
                }

                int err = Marshal.GetLastWin32Error();
                if (err == Native.ERROR_IO_PENDING)
                {
                    _pending = true;
                    return true;
                }

                // 真正的失败
                _pending = false;
                return true;
            }

            /// <summary>等待完成，返回传输字节数；仍在挂起或失败返回 -1。</summary>
            public int Wait(int ms)
            {
                if (!_alive) return -1;

                if (_pending)
                {
                    uint w = Native.WaitForSingleObject(_event, (uint)ms);
                    if (w != Native.WAIT_OBJECT_0) return -1;   // 还挂着，交给 Dispose 取消
                    _pending = false;
                }

                int transferred;
                if (!Native.GetOverlappedResult(_handle, _ov, out transferred, false)) return -1;
                if (transferred > _length) transferred = _length;

                if (transferred > 0) Marshal.Copy(_raw, Buffer, 0, transferred);
                return transferred;
            }

            public void Dispose()
            {
                if (!_alive && _raw == IntPtr.Zero && _event == IntPtr.Zero) return;

                if (_pending && _handle != IntPtr.Zero)
                {
                    Native.CancelIo(_handle);
                    int transferred;
                    // 等取消真正落地后才能释放 OVERLAPPED 和缓冲区
                    Native.GetOverlappedResult(_handle, _ov, out transferred, true);
                    _pending = false;
                }

                _alive = false;

                if (_event != IntPtr.Zero)
                {
                    Native.CloseHandle(_event);
                    _event = IntPtr.Zero;
                }
                if (_raw != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_raw);
                    _raw = IntPtr.Zero;
                }
                if (_ov != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_ov);
                    _ov = IntPtr.Zero;
                }
            }
        }
    }

    #endregion

    #region SPDT（左/右键的开关状态，存在鼠标 EEPROM 地址 8）

    internal sealed class SpdtState
    {
        public bool Ok;
        public bool Left;
        public bool Right;
        public int KeyOperationByte = -1;   // EEPROM 地址 8
        public int SecondByte = -1;         // EEPROM 地址 9
        public string Error = "";
        public byte[] Raw;

        public string Describe()
        {
            if (!Ok) return "读取失败：" + Error;
            return "左键 SPDT = " + (Left ? "开" : "关")
                + "，右键 SPDT = " + (Right ? "开" : "关")
                + "（EEPROM[8]=0x" + KeyOperationByte.ToString("X2")
                + "，[9]=0x" + (SecondByte < 0 ? "??" : SecondByte.ToString("X2")) + "）";
        }
    }

    internal static class Spdt
    {
        /// <summary>读 SPDT 状态（只发读命令，绝对安全）。</summary>
        public static SpdtState Read(HidInterface item)
        {
            SpdtState state = new SpdtState();

            byte[] reply;
            string error;
            byte[] request = CompxProtocol.BuildEepromRead(CompxProtocol.KeyOperationAddress, 2);

            if (!BatteryClient.ExchangeCompx(item, request, out reply, out error))
            {
                state.Error = error;
                return state;
            }

            state.Raw = reply;

            if (reply.Length < 8)
            {
                state.Error = "回复太短：" + Util.Hex(reply, reply.Length);
                return state;
            }

            // compx 回复布局：raw[0]=报告 ID，raw[1]=命令 echo，数据从 raw[6] 开始
            state.KeyOperationByte = reply[6];
            state.SecondByte = reply[7];
            state.Left = CompxProtocol.SpdtLeft(state.KeyOperationByte);
            state.Right = CompxProtocol.SpdtRight(state.KeyOperationByte);
            state.Ok = true;
            return state;
        }

        /// <summary>
        /// 读-改-写：先读回当前值，只改指定的位，写回后再回读校验。
        /// 传 null 表示该键保持原样。两个键都要改就一次写完。
        /// </summary>
        public static SpdtState Write(HidInterface item, bool? left, bool? right, out string error)
        {
            error = "";

            SpdtState before = Read(item);
            if (!before.Ok)
            {
                error = "写入前先读取失败：" + before.Error;
                return null;
            }

            int value = before.KeyOperationByte & 0xFF;
            if (left.HasValue)
                value = left.Value ? (value | CompxProtocol.SpdtLeftBit) : (value & ~CompxProtocol.SpdtLeftBit);
            if (right.HasValue)
                value = right.Value ? (value | CompxProtocol.SpdtRightBit) : (value & ~CompxProtocol.SpdtRightBit);

            if ((value & 3) == (before.KeyOperationByte & 3))
            {
                return before;      // 本来就是这个状态，不必写 EEPROM
            }

            byte[] reply;
            string writeError;
            byte[] request = CompxProtocol.BuildEepromWrite(CompxProtocol.KeyOperationAddress, value, 2);

            if (!BatteryClient.ExchangeCompx(item, request, out reply, out writeError))
            {
                error = "写入失败：" + writeError;
                return null;
            }

            // EEPROM 写完留一点时间再回读
            Thread.Sleep(200);

            SpdtState after = Read(item);
            if (!after.Ok)
            {
                error = "写入后回读失败：" + after.Error;
                return null;
            }

            if ((after.KeyOperationByte & 3) != (value & 3))
            {
                error = "写入后回读不一致：期望 0x" + (value & 3).ToString("X2")
                    + "，实际 0x" + (after.KeyOperationByte & 0xFF).ToString("X2");
                return after;
            }

            return after;
        }
    }

    #endregion

    #region EEPROM 通用读写（compx）

    internal static class Eeprom
    {
        /// <summary>单次读最多 10 字节（一帧只有 16 字节载荷）。</summary>
        public const int MaxChunk = 10;

        public static bool Read(HidInterface item, int address, int length, out byte[] data, out string error)
        {
            data = new byte[length];
            error = "";

            int done = 0;
            while (done < length)
            {
                int chunk = length - done;
                if (chunk > MaxChunk) chunk = MaxChunk;

                byte[] reply;
                if (!BatteryClient.ExchangeCompx(item,
                        CompxProtocol.BuildEepromRead(address + done, chunk), out reply, out error))
                    return false;

                // 回复布局：raw[1]=命令 echo，数据从 raw[6] 开始
                if (reply.Length < 6 + chunk)
                {
                    error = "回复太短（需要 " + (6 + chunk) + " 字节，实际 " + reply.Length + "）";
                    return false;
                }

                Array.Copy(reply, 6, data, done, chunk);
                done += chunk;
            }

            return true;
        }

        public static bool Write(HidInterface item, int address, byte[] data, out string error)
        {
            error = "";

            int done = 0;
            while (done < data.Length)
            {
                int chunk = data.Length - done;
                if (chunk > MaxChunk) chunk = MaxChunk;

                byte[] piece = new byte[chunk];
                Array.Copy(data, done, piece, 0, chunk);

                byte[] reply;
                if (!BatteryClient.ExchangeCompx(item,
                        CompxProtocol.BuildEepromWriteArray(address + done, piece), out reply, out error))
                    return false;

                done += chunk;
                Thread.Sleep(40);
            }

            return true;
        }

        /// <summary>按驱动里的格式读一个"值"（长度 2，低位在前）。</summary>
        public static bool ReadWord(HidInterface item, int address, out int value, out string error)
        {
            byte[] data;
            value = -1;
            if (!Read(item, address, 2, out data, out error)) return false;
            value = data[0] | (data[1] << 8);
            return true;
        }
    }

    #endregion

    #region 鼠标设置：灵敏度（DPI）与回报率

    internal sealed class DpiInfo
    {
        public bool Ok;
        public int StageCount = -1;
        public int CurrentStage = -1;
        public int CurrentDpi = -1;
        public int[] StageDpi;
        public string Error = "";
        public string RawHex;
    }

    internal sealed class RateInfo
    {
        public bool Ok;
        public int Code = -1;
        public int Hz = -1;
        public string Error = "";
        public string RawHex;
    }

    /// <summary>
    /// DPI 与回报率都存在鼠标 EEPROM 里（都取自网页驱动的实现）：
    ///   * 回报率：地址 0，2 字节，值 = Hz / 125（1/2/4/8/16/32/64 → 125…8000）
    ///   * DPI   ：地址 6912 + 档位*6，每档 6 字节（详细布局见 docs/协议说明.md）
    ///             [x低, x高, y低, y高, (高位&lt;&lt;2)|(高位&lt;&lt;6), 校验]
    ///             校验 = 85 - 前 5 字节之和，编码 code = DPI - 1
    /// </summary>
    internal static class MouseSettings
    {
        /// <summary>
        /// DPI 编码：18 位、步进 1，即 code = DPI - 1（布局见 docs/协议说明.md）。
        /// 按真实字节反推验证过：
        ///   1F 03 1F 03 00 11 → 799+1  = 800 DPI
        ///   3F 06 3F 06 00 CB → 1599+1 = 1600 DPI
        /// 注意：地址 12 那一片是废弃的旧表（4 字节 / 步进 50），固件不读它，
        /// 往那里写不会有任何效果。
        /// </summary>
        public const int DpiStep = 1;

        /// <summary>DPIMax = 40000（18 位编码本身能到 262143）。</summary>
        public const int DpiMax = 40000;

        public const int MaxStages = 7;

        /// <summary>DPI 值区：起始地址 6912，每档 6 字节。</summary>
        public const int DpiAreaBase = 6912;
        public const int DpiStageStride = 6;

        /// <summary>菜单里"增大/减小"按钮用的步长（编码步进是 1，但按钮按 50 更好点）。</summary>
        public const int DpiButtonStep = 50;

        public static readonly int[] RateOptions = new int[] { 125, 250, 500, 1000, 2000, 4000, 8000 };
        public static readonly int[] DpiPresets = new int[] { 400, 800, 1200, 1600, 2400, 3200, 6400, 12800, 25600, 40000 };

        #region 配置档 / 重新加载

        /// <summary>读当前配置档（1 起算），失败返回 -1。</summary>
        public static int GetProfile(HidInterface item)
        {
            byte[] reply;
            string error;

            if (!BatteryClient.ExchangeCompx(item, CompxProtocol.BuildGetCurrentConfig(),
                    out reply, out error))
                return -1;

            if (reply.Length < 8) return -1;
            return reply[6] + 1;
        }

        /// <summary>
        /// 让刚写进 EEPROM 的设置生效：重新选一次当前配置档，让设备重读配置。
        /// 这只是切到"同一个档"，不会改变用户的选择。
        /// </summary>
        public static bool ApplySettings(HidInterface item, out string error)
        {
            error = "";

            int profile = GetProfile(item);
            if (profile < 1 || profile > 5)
            {
                error = "读不到当前配置档（返回 " + profile + "）";
                return false;
            }

            byte[] reply;
            if (!BatteryClient.ExchangeCompx(item, CompxProtocol.BuildSetCurrentConfig(profile),
                    out reply, out error))
            {
                error = "重新加载失败：" + error;
                return false;
            }

            Thread.Sleep(300);
            return true;
        }

        #endregion

        #region DPI

        public static byte DpiBlockCrc(byte a, byte b, byte c, byte d, byte e)
        {
            return (byte)((85 - ((a + b + c + d + e) & 0xFF)) & 0xFF);
        }

        /// <summary>
        /// 把 DPI 编成 6 字节块（X/Y 同值），布局取自驱动的 3955 分支：
        ///   [X低, X高, Y低, Y高, (X高2位&lt;&lt;2)|(Y高2位&lt;&lt;6), 校验]
        /// </summary>
        public static byte[] BuildDpiBlock(int dpi)
        {
            int code = dpi / DpiStep - 1;
            byte lo = (byte)(code & 0xFF);
            byte hi = (byte)((code >> 8) & 0xFF);
            byte top = (byte)((code >> 16) & 0x03);
            byte fifth = (byte)((top << 2) | (top << 6));

            return new byte[] { lo, hi, lo, hi, fifth, DpiBlockCrc(lo, hi, lo, hi, fifth) };
        }

        public static int DecodeDpi(byte[] block, int offset)
        {
            int code = block[offset]
                | (block[offset + 1] << 8)
                | (((block[offset + 4] & 0x0C) >> 2) << 16);

            return (code + 1) * DpiStep;
        }

        public static bool IsValidDpi(int dpi)
        {
            return dpi >= 1 && dpi <= DpiMax;
        }

        public static DpiInfo ReadDpi(HidInterface item)
        {
            DpiInfo info = new DpiInfo();
            string error;

            // 地址 2 = 档位数，地址 4 = 当前档，一次读回来（各带一个"85-值"补偿字节）
            byte[] head;
            if (!Eeprom.Read(item, CompxProtocol.MaxDpiStageAddress, 4, out head, out error))
            {
                info.Error = error;
                return info;
            }

            info.StageCount = head[0];
            info.CurrentStage = head[2];

            if (info.StageCount < 1 || info.StageCount > MaxStages) info.StageCount = MaxStages;
            if (info.CurrentStage < 0 || info.CurrentStage >= info.StageCount) info.CurrentStage = 0;

            // DPI 值区：永远读满 7 档，方便展示
            byte[] values;
            if (!Eeprom.Read(item, DpiAreaBase, MaxStages * DpiStageStride, out values, out error))
            {
                info.Error = error;
                return info;
            }

            info.StageDpi = new int[MaxStages];
            for (int i = 0; i < MaxStages; i++)
                info.StageDpi[i] = DecodeDpi(values, i * DpiStageStride);

            info.CurrentDpi = info.StageDpi[info.CurrentStage];
            info.RawHex = Util.Hex(values, values.Length);
            info.Ok = true;
            return info;
        }

        /// <summary>
        /// 让设备把刚写进 EEPROM 的配置重新应用到传感器上。
        ///
        /// 官方驱动在更新配置时会**原样重写一遍** `Ge.maxDpiStage`（地址 2）与
        /// `Ge.CurrentDPI`（地址 4）—— 值不变，但很可能正是这两步让固件重新加载 DPI 表。
        /// 顺带再切一次当前配置档（同一个档，不改变用户选择）。
        /// </summary>
        public static void ApplyTriggers(HidInterface item, int stageCount, int currentStage)
        {
            string ignore;

            if (stageCount >= 1 && stageCount <= MaxStages)
            {
                Eeprom.Write(item, CompxProtocol.MaxDpiStageAddress,
                    new byte[] { (byte)stageCount, (byte)(85 - stageCount) }, out ignore);
            }

            if (currentStage >= 0 && currentStage < MaxStages)
            {
                Eeprom.Write(item, CompxProtocol.CurrentDpiAddress,
                    new byte[] { (byte)currentStage, (byte)(85 - currentStage) }, out ignore);
            }

            Thread.Sleep(120);

            ApplySettings(item, out ignore);
            Thread.Sleep(150);
        }

        /// <summary>写某一档的 DPI，写完立刻回读校验，再触发设备重新加载。</summary>
        public static bool WriteDpi(HidInterface item, int stage, int dpi, out string error)
        {
            error = "";

            if (!IsValidDpi(dpi))
            {
                error = "DPI 必须在 1~" + DpiMax + " 之间";
                return false;
            }
            if (stage < 0 || stage >= MaxStages)
            {
                error = "档位必须在 0~" + (MaxStages - 1) + " 之间";
                return false;
            }

            // 先记下档位数与当前档，稍后原样重写（触发设备重新加载）
            int stageCount = 1;
            int currentStage = stage;
            string readError;
            byte[] head;
            if (Eeprom.Read(item, CompxProtocol.MaxDpiStageAddress, 4, out head, out readError))
            {
                if (head[0] >= 1 && head[0] <= MaxStages) stageCount = head[0];
                if (head[2] < MaxStages) currentStage = head[2];
            }

            byte[] block = BuildDpiBlock(dpi);
            int address = DpiAreaBase + stage * DpiStageStride;

            if (!Eeprom.Write(item, address, block, out error)) return false;

            Thread.Sleep(150);

            byte[] check;
            if (!Eeprom.Read(item, address, DpiStageStride, out check, out error)) return false;

            for (int i = 0; i < DpiStageStride; i++)
            {
                if (check[i] != block[i])
                {
                    error = "回读不一致：写入 " + Util.Hex(block, DpiStageStride)
                        + "，读到 " + Util.Hex(check, DpiStageStride);
                    return false;
                }
            }

            ApplyTriggers(item, stageCount, currentStage);
            return true;
        }

        #endregion

        #region 回报率

        public static int RateCodeToHz(int code)
        {
            return code * 125;
        }

        public static int RateHzToCode(int hz)
        {
            return hz / 125;
        }

        public static bool IsValidRate(int hz)
        {
            foreach (int option in RateOptions) if (option == hz) return true;
            return false;
        }

        public static RateInfo ReadRate(HidInterface item)
        {
            RateInfo info = new RateInfo();
            string error;

            byte[] data;
            if (!Eeprom.Read(item, CompxProtocol.ReportRateAddress, 2, out data, out error))
            {
                info.Error = error;
                return info;
            }

            info.Code = data[0];
            info.RawHex = Util.Hex(data, 2);
            info.Ok = true;

            foreach (int option in RateOptions)
            {
                if (RateCodeToHz(info.Code) == option)
                {
                    info.Hz = option;
                    break;
                }
            }

            return info;
        }

        public static bool WriteRate(HidInterface item, int hz, out string error)
        {
            error = "";

            if (!IsValidRate(hz))
            {
                error = "回报率只能是 " + string.Join(" / ", Array.ConvertAll(RateOptions, delegate (int v) { return v.ToString(); })) + " Hz";
                return false;
            }

            int code = RateHzToCode(hz);

            byte[] reply;
            if (!BatteryClient.ExchangeCompx(item,
                    CompxProtocol.BuildEepromWrite(CompxProtocol.ReportRateAddress, code, 2),
                    out reply, out error))
                return false;

            Thread.Sleep(150);

            byte[] check;
            if (!Eeprom.Read(item, CompxProtocol.ReportRateAddress, 2, out check, out error)) return false;

            if (check[0] != (byte)code)
            {
                error = "回读不一致：写入 0x" + code.ToString("X2") + "，读到 0x" + check[0].ToString("X2");
                return false;
            }

            string applyError;
            ApplySettings(item, out applyError);

            return true;
        }

        #endregion
    }

    #endregion

    #region 工具

    internal static class Util
    {
        /// <summary>
        /// 统一设备接口路径前缀。HID 接口路径必须用 \\.\ 前缀；
        /// 路径里只要出现 '?' 字符，CreateFile 必定失败并返回 ERROR_PATH_NOT_FOUND。
        /// 上游给的前缀可能是 \\.\、\\?\、\??\ 甚至叠加，所以先剥干净再统一补。
        /// </summary>
        public static string NormalizeDevicePath(string path)
        {
            if (path == null) return null;

            int i = 0;
            while (i < path.Length && (path[i] == '\\' || path[i] == '.' || path[i] == '?')) i++;

            return @"\\.\" + path.Substring(i);
        }

        public static int ClampPercent(int value)
        {
            if (value < 0) return 0;
            if (value > 100) return 100;
            return value;
        }

        /// <summary>0-10 严重不足；11-25 偏低；26-99 正常；100 已充满。</summary>
        public static string StateText(int percent)
        {
            if (percent < 0) return "未知";
            if (percent <= 10) return "严重不足";
            if (percent <= 25) return "电量偏低";
            if (percent >= 100) return "已充满";
            return "正常";
        }

        public static string JsonEscape(string s)
        {
            if (s == null) return "null";

            StringBuilder sb = new StringBuilder();
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        public static string FormatMv(int mv)
        {
            if (mv < 0) return "未知";
            return (mv / 1000.0).ToString("0.000") + " V";
        }

        public static string Hex(byte[] buffer, int length)
        {
            if (buffer == null) return "";
            if (length > buffer.Length) length = buffer.Length;

            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < length; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(buffer[i].ToString("X2"));
            }
            return sb.ToString();
        }
    }

    internal sealed class AppSettings
    {
        public int PollSeconds = 5;
        public int IconStyle = 0;            // 0 = 纯数字(实心) / 1 = 电池+数字 / 2 = 纯数字(无底色)
        public bool AutoStart;
        public bool ShowTrayHint = true;     // 首次运行提示"去哪找托盘图标"

        private static string FilePath
        {
            get
            {
                string dir;
                try
                {
                    dir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                }
                catch
                {
                    dir = null;
                }
                if (string.IsNullOrEmpty(dir)) dir = ".";
                dir = System.IO.Path.Combine(dir, "GWMouseBattery");
                return System.IO.Path.Combine(dir, "settings.ini");
            }
        }

        public static AppSettings Load()
        {
            AppSettings s = new AppSettings();
            try
            {
                string path = FilePath;
                if (!System.IO.File.Exists(path)) return s;

                foreach (string line in System.IO.File.ReadAllLines(path, Encoding.UTF8))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim();
                    string value = line.Substring(eq + 1).Trim();

                    if (key == "pollSeconds") s.PollSeconds = ParseInt(value, s.PollSeconds);
                    else if (key == "iconStyle") s.IconStyle = ParseInt(value, s.IconStyle);
                    else if (key == "autoStart") s.AutoStart = value == "1";
                    else if (key == "showTrayHint") s.ShowTrayHint = value == "1";
                }
            }
            catch { }

            if (s.PollSeconds < 2) s.PollSeconds = 2;
            if (s.PollSeconds > 600) s.PollSeconds = 600;
            if (s.IconStyle < 0 || s.IconStyle > 2) s.IconStyle = 0;
            return s;
        }

        private static int ParseInt(string text, int fallback)
        {
            int v;
            if (int.TryParse(text, out v)) return v;
            return fallback;
        }

        public void Save()
        {
            try
            {
                string path = FilePath;
                string dir = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
                    System.IO.Directory.CreateDirectory(dir);

                StringBuilder sb = new StringBuilder();
                sb.AppendLine("pollSeconds=" + PollSeconds);
                sb.AppendLine("iconStyle=" + IconStyle);
                sb.AppendLine("autoStart=" + (AutoStart ? "1" : "0"));
                sb.AppendLine("showTrayHint=" + (ShowTrayHint ? "1" : "0"));
                System.IO.File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }
    }

    #endregion

    #region 自检（只测逻辑，绝不碰硬件 —— 免得一个坏设备把自检拖死）

    internal static class SelfTest
    {
        private static int _pass;
        private static int _fail;

        private static void Check(string name, bool ok, string detail)
        {
            if (ok)
            {
                _pass++;
                Cli.Write("  [PASS] " + name);
            }
            else
            {
                _fail++;
                Cli.Write("  [FAIL] " + name + "   -> " + detail);
            }
        }

        public static int Run()
        {
            // 一律走 Cli.Write：这样 --out 才能把自检结果也一起抓到文件里，
            // 否则用 "start /wait ... --out file" 拿到的是个空文件。
            Cli.Blank();
            Cli.Write("  G-Wolves 鼠标电量 · 核心逻辑自检（不访问硬件）");
            Cli.Blank();

            // ---- compx 帧 ----
            byte[] req = CompxProtocol.BuildBatteryRequest();
            Check("compx 帧长 17", req.Length == 17, req.Length.ToString());
            Check("compx 报告 ID = 8", req[0] == 0x08, req[0].ToString("X2"));
            Check("compx 命令 = 0x04", req[1] == 0x04, req[1].ToString("X2"));
            Check("compx 校验和 = 0x49", req[16] == 0x49, req[16].ToString("X2"));

            byte[] payload = new byte[16];
            payload[0] = 0x04;
            Check("compx 校验和函数与文档一致", CompxProtocol.Checksum(payload) == 0x49,
                CompxProtocol.Checksum(payload).ToString("X2"));

            // ---- compx 解析：文档里的真实样本 ----
            byte[] sample = new byte[] { 0x08, 0x04, 0x00, 0x00, 0x00, 0x02, 0x5A, 0x00,
                                         0x0F, 0xE3, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xCC };
            int pct, mv;
            bool chg;
            Check("compx 解析样本成功", CompxProtocol.TryParse(sample, out pct, out chg, out mv), "false");
            Check("compx 电量 = 90", pct == 90, pct.ToString());
            Check("compx 未充电", !chg, chg.ToString());
            Check("compx 电压 = 4067 mV", mv == 4067, mv.ToString());

            byte[] charged = new byte[] { 0x08, 0x04, 0x00, 0x00, 0x00, 0x02, 0x52, 0x01,
                                          0x0F, 0x7D, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xCC };
            Check("compx 解析充电状态", CompxProtocol.TryParse(charged, out pct, out chg, out mv) && chg
                && pct == 82 && mv == 3965, pct + "/" + chg + "/" + mv);

            byte[] wrongEcho = new byte[] { 0x08, 0x05, 0x00, 0x00, 0x00, 0x02, 0x5A, 0x00,
                                            0x0F, 0xE3, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xCC };
            Check("compx 拒绝命令 echo 不匹配的报文",
                !CompxProtocol.TryParse(wrongEcho, out pct, out chg, out mv), "true");

            byte[] tooBig = new byte[] { 0x08, 0x04, 0x00, 0x00, 0x00, 0x02, 0x7F, 0x00,
                                         0x0F, 0xE3, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xCC };
            Check("compx 拒绝 >100 的电量",
                !CompxProtocol.TryParse(tooBig, out pct, out chg, out mv), "true");

            Check("compx 拒绝 null", !CompxProtocol.TryParse(null, out pct, out chg, out mv), "true");
            Check("compx 拒绝过短报文",
                !CompxProtocol.TryParse(new byte[4], out pct, out chg, out mv), "true");

            // ---- feature 帧 ----
            byte[] freq = FeatureProtocol.BuildBatteryRequest(2, 65);
            Check("feature 帧长 65", freq.Length == 65, freq.Length.ToString());
            Check("feature 报告 ID = 0", freq[0] == 0, freq[0].ToString("X2"));
            Check("feature payload[2] = 设备号 2", freq[3] == 2, freq[3].ToString("X2"));
            Check("feature payload[3] = 2", freq[4] == 2, freq[4].ToString("X2"));
            Check("feature payload[5] = 0x83", freq[6] == 0x83, freq[6].ToString("X2"));

            byte[] fresp = new byte[65];
            fresp[1] = 0xA1;
            fresp[6] = 0x83;
            fresp[7] = 1;
            fresp[8] = 78;
            Check("feature 解析成功", FeatureProtocol.TryParse(fresp, out pct, out chg), "false");
            Check("feature 电量 = 78", pct == 78, pct.ToString());
            Check("feature 充电 = true", chg, chg.ToString());

            fresp[1] = 0x00;
            Check("feature 拒绝 header 不对的报文",
                !FeatureProtocol.TryParse(fresp, out pct, out chg), "true");

            // ---- 设备路径规范化（曾经导致所有设备都打不开的坑）----
            string good = Util.NormalizeDevicePath(@"\\.\hid#vid_33e4&pid_3854#7&1&0&0004#{guid}");
            Check("合法路径保持不变", good == @"\\.\hid#vid_33e4&pid_3854#7&1&0&0004#{guid}", good);

            foreach (string input in new string[] { @"\\?\hid#a", @"\??\hid#a", @"\\.\?\hid#a",
                                                    @"\\.\hid#a", "hid#a" })
            {
                string output = Util.NormalizeDevicePath(input);
                Check("  路径形态 " + input + " -> 无 '?' 且前缀正确",
                    output.IndexOf('?') < 0 && output.StartsWith(@"\\.\", StringComparison.Ordinal),
                    output);
            }

            Check("空路径不崩溃", Util.NormalizeDevicePath(null) == null, "null");

            // ---- 协议家族判定（只看描述符）----
            HidInterface c = new HidInterface();
            c.InputLength = 17;
            c.OutputLength = 17;
            c.FeatureLength = 0;
            Check("17/17 判定为 compx", HidDiscovery.Classify(c) == ProtocolKind.Compx, "?");

            HidInterface f = new HidInterface();
            f.InputLength = 8;
            f.OutputLength = 8;
            f.FeatureLength = 65;
            Check("65 字节 feature 判定为 feature", HidDiscovery.Classify(f) == ProtocolKind.Feature, "?");

            HidInterface n = new HidInterface();
            n.InputLength = 8;
            n.OutputLength = 8;
            n.FeatureLength = 0;
            Check("8/8 且无 feature 判定为未知", HidDiscovery.Classify(n) == ProtocolKind.Unknown, "?");

            HidInterface o = new HidInterface();
            o.InputLength = 33;
            o.OutputLength = 65;
            o.FeatureLength = 0;
            Check("33/65（触摸板）不误判为 compx",
                HidDiscovery.Classify(o) == ProtocolKind.Unknown, "?");

            // ---- compx EEPROM 帧（SPDT 走的就是它）----
            byte[] eread = CompxProtocol.BuildEepromRead(CompxProtocol.KeyOperationAddress, 2);
            Check("EEPROM 读帧长 17", eread.Length == 17, eread.Length.ToString());
            Check("EEPROM 读 报告 ID = 8", eread[0] == 0x08, eread[0].ToString("X2"));
            Check("EEPROM 读 命令 = 0x08", eread[1] == 0x08, eread[1].ToString("X2"));
            Check("EEPROM 读 地址高字节 = 0", eread[3] == 0, eread[3].ToString("X2"));
            Check("EEPROM 读 地址低字节 = 8", eread[4] == 8, eread[4].ToString("X2"));
            Check("EEPROM 读 长度 = 2", eread[5] == 2, eread[5].ToString("X2"));
            Check("EEPROM 读 校验和 = 0x3B", eread[16] == 0x3B, eread[16].ToString("X2"));

            byte[] ewrite = CompxProtocol.BuildEepromWrite(CompxProtocol.KeyOperationAddress, 1, 2);
            Check("EEPROM 写 命令 = 0x07", ewrite[1] == 0x07, ewrite[1].ToString("X2"));
            Check("EEPROM 写 地址低字节 = 8", ewrite[4] == 8, ewrite[4].ToString("X2"));
            Check("EEPROM 写 长度 = 2", ewrite[5] == 2, ewrite[5].ToString("X2"));
            Check("EEPROM 写 值 = 1", ewrite[6] == 1, ewrite[6].ToString("X2"));
            Check("EEPROM 写 伴随字节 = 85-1 = 84", ewrite[7] == 84, ewrite[7].ToString("X2"));
            Check("EEPROM 写 校验和 = 0xE7", ewrite[16] == 0xE7, ewrite[16].ToString("X2"));

            // ---- SPDT 位解码（bit0=左键，bit1=右键）----
            Check("SPDT 0x00 = 左关右关",
                !CompxProtocol.SpdtLeft(0) && !CompxProtocol.SpdtRight(0), "00");
            Check("SPDT 0x01 = 左开右关",
                CompxProtocol.SpdtLeft(1) && !CompxProtocol.SpdtRight(1), "01");
            Check("SPDT 0x02 = 左关右开",
                !CompxProtocol.SpdtLeft(2) && CompxProtocol.SpdtRight(2), "02");
            Check("SPDT 0x03 = 左开右开",
                CompxProtocol.SpdtLeft(3) && CompxProtocol.SpdtRight(3), "03");
            Check("SPDT 高位不影响判定",
                CompxProtocol.SpdtLeft(0xF1) && CompxProtocol.SpdtRight(0xF2), "F1/F2");

            // ---- DPI 编码（对照 docs/协议说明.md 里的字节验算）----
            Check("DPI 1200 -> AF 04 AF 04 00 EF",
                Util.Hex(MouseSettings.BuildDpiBlock(1200), 6) == "AF 04 AF 04 00 EF",
                Util.Hex(MouseSettings.BuildDpiBlock(1200), 6));
            Check("DPI 3200 -> 7F 0C 7F 0C 00 3F",
                Util.Hex(MouseSettings.BuildDpiBlock(3200), 6) == "7F 0C 7F 0C 00 3F",
                Util.Hex(MouseSettings.BuildDpiBlock(3200), 6));
            Check("DPI 400 -> 8F 01 8F 01 00 35",
                Util.Hex(MouseSettings.BuildDpiBlock(400), 6) == "8F 01 8F 01 00 35",
                Util.Hex(MouseSettings.BuildDpiBlock(400), 6));

            byte[] sampleDpi = new byte[]
            {
                0x1F, 0x03, 0x1F, 0x03, 0x00, 0x11,   // 800
                0x1F, 0x03, 0x1F, 0x03, 0x00, 0x11,   // 800
                0x3F, 0x06, 0x3F, 0x06, 0x00, 0xCB    // 1600
            };
            Check("解码样例档0 = 800", MouseSettings.DecodeDpi(sampleDpi, 0) == 800,
                MouseSettings.DecodeDpi(sampleDpi, 0).ToString());
            Check("解码样例档1 = 800", MouseSettings.DecodeDpi(sampleDpi, 6) == 800,
                MouseSettings.DecodeDpi(sampleDpi, 6).ToString());
            Check("解码样例档2 = 1600", MouseSettings.DecodeDpi(sampleDpi, 12) == 1600,
                MouseSettings.DecodeDpi(sampleDpi, 12).ToString());

            Check("DPI 编码往返一致（6400）",
                MouseSettings.DecodeDpi(MouseSettings.BuildDpiBlock(6400), 0) == 6400,
                MouseSettings.DecodeDpi(MouseSettings.BuildDpiBlock(6400), 0).ToString());
            Check("DPI 编码往返一致（40000）",
                MouseSettings.DecodeDpi(MouseSettings.BuildDpiBlock(40000), 0) == 40000,
                MouseSettings.DecodeDpi(MouseSettings.BuildDpiBlock(40000), 0).ToString());
            Check("DPI 1 可编码", MouseSettings.IsValidDpi(1), "1");
            Check("DPI 40000 可编码（机型上限）", MouseSettings.IsValidDpi(40000), "40000");
            Check("DPI 40001 被拒（超机型上限）", !MouseSettings.IsValidDpi(40001), "40001");
            Check("DPI 0 被拒", !MouseSettings.IsValidDpi(0), "0");

            // ---- 回报率编码 ----
            Check("回报率 16 -> 2000 Hz", MouseSettings.RateCodeToHz(16) == 2000,
                MouseSettings.RateCodeToHz(16).ToString());
            Check("回报率 2000 Hz -> 16", MouseSettings.RateHzToCode(2000) == 16,
                MouseSettings.RateHzToCode(2000).ToString());
            Check("回报率 8000 Hz -> 64", MouseSettings.RateHzToCode(8000) == 64,
                MouseSettings.RateHzToCode(8000).ToString());
            Check("回报率 125 Hz -> 1", MouseSettings.RateHzToCode(125) == 1,
                MouseSettings.RateHzToCode(125).ToString());
            Check("回报率 3000 被拒", !MouseSettings.IsValidRate(3000), "3000");
            Check("回报率 1000 可接受", MouseSettings.IsValidRate(1000), "1000");

            // ---- EEPROM 数组写入帧（DPI 用的那条路）----
            byte[] arrFrame = CompxProtocol.BuildEepromWriteArray(MouseSettings.DpiAreaBase,
                new byte[] { 0xAF, 0x04, 0xAF, 0x04, 0x00, 0xEF });
            Check("数组写帧长 17", arrFrame.Length == 17, arrFrame.Length.ToString());
            Check("数组写 命令 = 0x07", arrFrame[1] == 0x07, arrFrame[1].ToString("X2"));
            Check("数组写 地址 = 6912（0x1B00）",
                arrFrame[3] == 0x1B && arrFrame[4] == 0x00,
                arrFrame[3].ToString("X2") + " " + arrFrame[4].ToString("X2"));
            Check("数组写 长度 = 6", arrFrame[5] == 6, arrFrame[5].ToString("X2"));
            Check("数组写 数据 = AF 04 AF 04 00 EF",
                arrFrame[6] == 0xAF && arrFrame[7] == 0x04 && arrFrame[8] == 0xAF
                && arrFrame[9] == 0x04 && arrFrame[10] == 0x00 && arrFrame[11] == 0xEF,
                Util.Hex(arrFrame, 12));

            // ---- 电量状态分档 ----
            Check("0% 严重不足", Util.StateText(0) == "严重不足", Util.StateText(0));
            Check("10% 严重不足", Util.StateText(10) == "严重不足", Util.StateText(10));
            Check("11% 偏低", Util.StateText(11) == "电量偏低", Util.StateText(11));
            Check("25% 偏低", Util.StateText(25) == "电量偏低", Util.StateText(25));
            Check("26% 正常", Util.StateText(26) == "正常", Util.StateText(26));
            Check("99% 正常", Util.StateText(99) == "正常", Util.StateText(99));
            Check("100% 已充满", Util.StateText(100) == "已充满", Util.StateText(100));
            Check("-1 未知", Util.StateText(-1) == "未知", Util.StateText(-1));
            Check("127 夹紧为 100", Util.ClampPercent(127) == 100, Util.ClampPercent(127).ToString());
            Check("-5 夹紧为 0", Util.ClampPercent(-5) == 0, Util.ClampPercent(-5).ToString());

            // ---- JSON 转义 ----
            Check("JSON 转义引号", Util.JsonEscape("a\"b") == "\"a\\\"b\"", Util.JsonEscape("a\"b"));
            Check("JSON 转义反斜杠", Util.JsonEscape("a\\b") == "\"a\\\\b\"", Util.JsonEscape("a\\b"));
            Check("JSON 转义换行", Util.JsonEscape("a\nb") == "\"a\\nb\"", Util.JsonEscape("a\nb"));
            Check("JSON 保留中文", Util.JsonEscape("鼠标") == "\"鼠标\"", Util.JsonEscape("鼠标"));
            Check("JSON null", Util.JsonEscape(null) == "null", Util.JsonEscape(null));

            // ---- 电压格式化 ----
            Check("电压 3980 mV -> 3.980 V", Util.FormatMv(3980) == "3.980 V", Util.FormatMv(3980));
            Check("电压未知", Util.FormatMv(-1) == "未知", Util.FormatMv(-1));

            Cli.Blank();
            Cli.Write("  结果: " + _pass + " 项通过, " + _fail + " 项失败");
            Cli.Blank();
            return _fail == 0 ? 0 : 1;
        }
    }

    #endregion
}
