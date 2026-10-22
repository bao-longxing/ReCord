using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace ReCord.Services
{
    public sealed class IpScanCandidate
    {
        public string IpAddress { get; set; }
        public int Port { get; set; }
        public string SuggestedUrl { get; set; }
        public string Description { get; set; }
        public bool IsOnvif { get; set; }
        public bool HasResolvedStream { get; set; }
        public string DeviceName { get; set; }
        public string Manufacturer { get; set; }
        public string Model { get; set; }
        public string ProfileName { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }

        public string DisplayName
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(DeviceName))
                    return DeviceName;
                return string.IsNullOrWhiteSpace(IpAddress) ? "未知相机" : IpAddress;
            }
        }

        public string SourceLabel { get { return IsOnvif ? "ONVIF" : "RTSP"; } }

        public string DisplayUrl
        {
            get
            {
                Uri uri;
                if (!Uri.TryCreate(SuggestedUrl, UriKind.Absolute, out uri))
                    return SuggestedUrl;
                try
                {
                    return new UriBuilder(uri)
                    {
                        UserName = string.Empty,
                        Password = string.Empty
                    }.Uri.AbsoluteUri;
                }
                catch
                {
                    return SuggestedUrl;
                }
            }
        }

        public string Endpoint
        {
            get
            {
                return string.IsNullOrWhiteSpace(IpAddress)
                    ? string.Empty
                    : IpAddress + (Port > 0 ? ":" + Port : string.Empty);
            }
        }
    }

    public sealed class IpScanProgress
    {
        public int Completed { get; set; }
        public int Total { get; set; }
        public int Found { get; set; }
        public string CurrentAddress { get; set; }
        public string Phase { get; set; }
    }

    public static class IpScanPhase
    {
        public const string Onvif = "ONVIF";
        public const string Ip = "IP";
    }

    public static class IpCameraScanner
    {
        private static readonly int[] RtspPorts = new[] { 554, 8554 };

        /// <summary>获取本机活动的 IPv4 网段，无法读取时返回常见默认网段。</summary>
        public static IList<string> GetLocalSubnets()
        {
            List<string> result = new List<string>();
            try
            {
                foreach (NetworkInterface network in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (network.OperationalStatus != OperationalStatus.Up ||
                        network.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                        network.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                        continue;

                    foreach (UnicastIPAddressInformation address in network.GetIPProperties().UnicastAddresses)
                    {
                        if (address.Address.AddressFamily != AddressFamily.InterNetwork ||
                            IPAddress.IsLoopback(address.Address))
                            continue;

                        byte[] bytes = address.Address.GetAddressBytes();
                        if (bytes[0] == 169 && bytes[1] == 254)
                            continue;

                        int prefix = address.PrefixLength;
                        if (prefix < 8 || prefix > 30)
                            prefix = 24;
                        string subnet = address.Address + "/" + prefix;
                        if (!result.Contains(subnet))
                            result.Add(subnet);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Warning("读取本机网段失败：" + ex.Message);
            }
            if (result.Count == 0)
                result.Add("192.168.1.0/24");
            return result;
        }

        /// <summary>在不使用 ONVIF 凭据的情况下启动设备扫描。</summary>
        public static Task<IList<IpScanCandidate>> ScanAsync(
            string range,
            IProgress<IpScanProgress> progress,
            CancellationToken cancellationToken)
        {
            return ScanAsync(range, null, progress, cancellationToken);
        }

        /// <summary>结合 ONVIF 发现和 RTSP 端口探测扫描指定 IPv4 网段。</summary>
        public static async Task<IList<IpScanCandidate>> ScanAsync(
            string range,
            OnvifCredentials credentials,
            IProgress<IpScanProgress> progress,
            CancellationToken cancellationToken)
        {
            List<IPAddress> addresses = ParseRange(range);
            if (addresses.Count == 0)
                throw new ArgumentException("请输入有效的 IPv4 网段，例如 192.168.1.0/24。", "range");

            Report(progress, IpScanPhase.Onvif, 0, 0, 0, "正在搜索 ONVIF 设备...");
            IList<OnvifDevice> devices = await OnvifDiscoveryService.DiscoverAsync(
                credentials,
                new OnvifProgressRelay(progress),
                cancellationToken);

            List<IpScanCandidate> onvifCandidates = new List<IpScanCandidate>();
            foreach (OnvifDevice device in devices)
            {
                IpScanCandidate candidate = CreateOnvifCandidate(device, credentials);
                if (candidate != null)
                    onvifCandidates.Add(candidate);
            }

            Report(progress, IpScanPhase.Onvif, 0, 0, onvifCandidates.Count,
                onvifCandidates.Count == 0
                    ? "未通过 ONVIF 发现设备，继续扫描 RTSP 端口"
                    : "ONVIF 发现完成，继续扫描 RTSP 端口");

            List<IpScanCandidate> rtspCandidates = await ScanAddressesAsync(
                addresses, progress, cancellationToken, onvifCandidates.Count);
            List<IpScanCandidate> merged = MergeCandidates(
                onvifCandidates, rtspCandidates, credentials);
            foreach (IpScanCandidate candidate in merged)
            {
                AppLog.Info("发现相机 " + candidate.SourceLabel + " " +
                            candidate.Endpoint + "：" + candidate.DisplayUrl);
            }
            return merged;
        }

        /// <summary>并发探测网段内各地址的可用 RTSP 端口。</summary>
        private static async Task<List<IpScanCandidate>> ScanAddressesAsync(
            List<IPAddress> addresses,
            IProgress<IpScanProgress> progress,
            CancellationToken cancellationToken,
            int initialFound)
        {
            HashSet<string> localAddresses = new HashSet<string>();
            try
            {
                localAddresses = new HashSet<string>(Dns.GetHostAddresses(Dns.GetHostName())
                    .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => a.ToString()));
            }
            catch (Exception ex)
            {
                AppLog.Warning("读取本机地址失败：" + ex.Message);
            }

            bool singleAddress = addresses.Count == 1;
            List<IpScanCandidate> found = new List<IpScanCandidate>();
            object resultLock = new object();
            int completed = 0;
            SemaphoreSlim gate = new SemaphoreSlim(48, 48);
            try
            {
                List<Task> tasks = new List<Task>();
                foreach (IPAddress address in addresses)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!singleAddress && localAddresses.Contains(address.ToString()))
                        continue;

                    IPAddress captured = address;
                    tasks.Add(Task.Run(async () =>
                    {
                        await gate.WaitAsync(cancellationToken);
                        try
                        {
                            IpScanCandidate candidate = await ProbeAddressAsync(captured, cancellationToken);
                            if (candidate != null)
                            {
                                lock (resultLock) found.Add(candidate);
                            }
                        }
                        finally
                        {
                            gate.Release();
                            int done = Interlocked.Increment(ref completed);
                            int foundCount;
                            lock (resultLock) foundCount = initialFound + found.Count;
                            Report(progress, IpScanPhase.Ip, done, addresses.Count,
                                foundCount, captured.ToString());
                        }
                    }, cancellationToken));
                }
                await Task.WhenAll(tasks);
            }
            finally
            {
                gate.Dispose();
            }
            return found;
        }

        /// <summary>将 ONVIF 设备和最佳媒体配置转换为扫描结果。</summary>
        private static IpScanCandidate CreateOnvifCandidate(
            OnvifDevice device,
            OnvifCredentials credentials)
        {
            if (device == null)
                return null;

            string host = OnvifDiscoveryService.DeviceHost(device);
            if (string.IsNullOrWhiteSpace(host))
                return null;

            OnvifStreamProfile profile = device.Profiles
                .Where(p => !string.IsNullOrWhiteSpace(p.StreamUri))
                .OrderByDescending(p => (long)p.Width * p.Height)
                .ThenByDescending(p => p.FrameRate)
                .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
                .FirstOrDefault();

            string deviceName = !string.IsNullOrWhiteSpace(device.Name)
                ? device.Name.Trim()
                : string.Join(" ", new[] { device.Manufacturer, device.Model }
                    .Where(v => !string.IsNullOrWhiteSpace(v)));
            if (string.IsNullOrWhiteSpace(deviceName))
                deviceName = "ONVIF 相机 " + host;

            string url;
            int port = 554;
            if (profile != null)
            {
                url = OnvifDiscoveryService.AddCredentialsToStreamUri(
                    profile.StreamUri, credentials);
                Uri parsed;
                if (Uri.TryCreate(url, UriKind.Absolute, out parsed) && parsed.Port > 0)
                    port = parsed.Port;
            }
            else
            {
                try
                {
                    url = new UriBuilder("rtsp", host, 554, "/").Uri.AbsoluteUri;
                }
                catch
                {
                    url = "rtsp://" + host + "/";
                }
            }

            List<string> details = new List<string>();
            if (profile != null)
            {
                if (!string.IsNullOrWhiteSpace(profile.Name))
                    details.Add(profile.Name.Trim());
                if (profile.Width > 0 && profile.Height > 0)
                    details.Add(profile.Width + "x" + profile.Height);
            }
            else if (!string.IsNullOrWhiteSpace(device.ErrorMessage))
            {
                details.Add(device.ErrorMessage.IndexOf("认证", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            device.ErrorMessage.IndexOf("用户名", StringComparison.OrdinalIgnoreCase) >= 0
                    ? "需要认证"
                    : "等待补充流地址");
            }
            else
            {
                details.Add("未解析到媒体码流");
            }

            return new IpScanCandidate
            {
                IpAddress = host,
                Port = port,
                SuggestedUrl = url,
                Description = string.Join(" · ", details),
                IsOnvif = true,
                HasResolvedStream = profile != null,
                DeviceName = deviceName,
                Manufacturer = device.Manufacturer,
                Model = device.Model,
                ProfileName = profile == null ? null : profile.Name,
                Width = profile == null ? 0 : profile.Width,
                Height = profile == null ? 0 : profile.Height
            };
        }

        /// <summary>合并 ONVIF 与 RTSP 结果并优先保留设备发现信息。</summary>
        private static List<IpScanCandidate> MergeCandidates(
            List<IpScanCandidate> onvifCandidates,
            List<IpScanCandidate> rtspCandidates,
            OnvifCredentials credentials)
        {
            List<IpScanCandidate> result = new List<IpScanCandidate>(onvifCandidates);
            foreach (IpScanCandidate rtsp in rtspCandidates)
            {
                IpScanCandidate onvif = result.FirstOrDefault(c => c.IsOnvif &&
                    string.Equals(c.IpAddress, rtsp.IpAddress, StringComparison.OrdinalIgnoreCase));
                if (onvif == null)
                {
                    result.Add(rtsp);
                    continue;
                }

                if (!onvif.HasResolvedStream)
                {
                    onvif.Port = rtsp.Port;
                    onvif.SuggestedUrl = OnvifDiscoveryService.AddCredentialsToStreamUri(
                        rtsp.SuggestedUrl, credentials);
                    onvif.Description = string.IsNullOrWhiteSpace(onvif.Description)
                        ? rtsp.Description
                        : onvif.Description + " · " + rtsp.Description;
                }
            }

            return result
                .OrderBy(c => ParseIpValue(c.IpAddress))
                .ThenBy(c => c.IsOnvif ? 0 : 1)
                .ThenBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        /// <summary>向调用方报告当前 IP 扫描进度。</summary>
        private static void Report(
            IProgress<IpScanProgress> progress,
            string phase,
            int completed,
            int total,
            int found,
            string current)
        {
            if (progress == null)
                return;
            progress.Report(new IpScanProgress
            {
                Phase = phase,
                Completed = completed,
                Total = total,
                Found = found,
                CurrentAddress = current
            });
        }

        private sealed class OnvifProgressRelay : IProgress<OnvifDiscoveryProgress>
        {
            private readonly IProgress<IpScanProgress> _progress;

            /// <summary>初始化 ONVIF 进度到 IP 扫描进度的转发器。</summary>
            public OnvifProgressRelay(IProgress<IpScanProgress> progress)
            {
                _progress = progress;
            }

            /// <summary>将 ONVIF 发现进度转换为统一的扫描进度。</summary>
            public void Report(OnvifDiscoveryProgress value)
            {
                if (_progress == null || value == null)
                    return;
                _progress.Report(new IpScanProgress
                {
                    Phase = IpScanPhase.Onvif,
                    Completed = value.DevicesResolved,
                    Total = value.DevicesTotal,
                    Found = value.DevicesFound,
                    CurrentAddress = value.Message
                });
            }
        }

        /// <summary>依次探测单个地址的常用 RTSP 端口。</summary>
        private static async Task<IpScanCandidate> ProbeAddressAsync(
            IPAddress address,
            CancellationToken cancellationToken)
        {
            foreach (int port in RtspPorts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await IsPortOpenAsync(address, port, 450, cancellationToken))
                {
                    return new IpScanCandidate
                    {
                        IpAddress = address.ToString(),
                        Port = port,
                        SuggestedUrl = port == 554
                            ? "rtsp://" + address + "/"
                            : "rtsp://" + address + ":" + port + "/",
                        Description = port == 554 ? "RTSP 标准端口" : "RTSP 备用端口"
                    };
                }
            }
            return null;
        }

        /// <summary>在超时和取消控制下检测 TCP 端口是否可连接。</summary>
        private static async Task<bool> IsPortOpenAsync(
            IPAddress address,
            int port,
            int timeoutMs,
            CancellationToken cancellationToken)
        {
            using (TcpClient client = new TcpClient())
            {
                Task connectTask = client.ConnectAsync(address, port);
                Task completed = await Task.WhenAny(connectTask, Task.Delay(timeoutMs, cancellationToken));
                if (completed != connectTask)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return false;
                }
                try
                {
                    await connectTask;
                    return client.Connected;
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>将单个 IPv4 地址或 CIDR 网段解析为可扫描地址列表。</summary>
        public static List<IPAddress> ParseRange(string range)
        {
            List<IPAddress> addresses = new List<IPAddress>();
            if (string.IsNullOrWhiteSpace(range))
                return addresses;
            range = range.Trim();

            IPAddress single;
            if (IPAddress.TryParse(range, out single) &&
                single.AddressFamily == AddressFamily.InterNetwork)
            {
                addresses.Add(single);
                return addresses;
            }

            string text = range;
            int prefix = 24;
            int slash = text.IndexOf('/');
            IPAddress networkAddress;
            if (slash >= 0)
            {
                if (!int.TryParse(text.Substring(slash + 1), out prefix) || prefix < 16 || prefix > 30)
                    return addresses;
                text = text.Substring(0, slash).Trim();
            }

            if (!IPAddress.TryParse(text, out networkAddress) ||
                networkAddress.AddressFamily != AddressFamily.InterNetwork)
                return addresses;

            uint network = IpToUInt(networkAddress);
            uint mask = prefix == 0 ? 0u : 0xFFFFFFFFu << (32 - prefix);
            uint first = (network & mask) + 1;
            uint last = (network & mask) | ~mask;
            last -= 1;
            if (last < first)
                return addresses;
            uint count = last - first + 1;
            if (count > 1024)
                return addresses;
            for (uint value = first; value <= last; value++)
                addresses.Add(UIntToIp(value));
            return addresses;
        }

        /// <summary>将 IPv4 地址转换为无符号整数。</summary>
        private static uint IpToUInt(IPAddress address)
        {
            byte[] bytes = address.GetAddressBytes();
            return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) |
                   ((uint)bytes[2] << 8) | bytes[3];
        }

        /// <summary>将无符号整数转换为 IPv4 地址。</summary>
        private static IPAddress UIntToIp(uint value)
        {
            return new IPAddress(new byte[]
            {
                (byte)(value >> 24),
                (byte)(value >> 16),
                (byte)(value >> 8),
                (byte)value
            });
        }

        /// <summary>将 IP 文本转换为用于排序的数值。</summary>
        private static uint ParseIpValue(string value)
        {
            IPAddress address;
            return IPAddress.TryParse(value, out address) ? IpToUInt(address) : 0;
        }
    }
}
