using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace ReCord.Services
{
    public sealed class OnvifCredentials
    {
        /// <summary>初始化可选的 ONVIF 用户名和密码凭据。</summary>
        public OnvifCredentials(string username, string password)
        {
            Username = username ?? string.Empty;
            Password = password ?? string.Empty;
        }

        public string Username { get; private set; }
        public string Password { get; private set; }
        public bool HasUsername { get { return !string.IsNullOrWhiteSpace(Username); } }
    }

    public sealed class OnvifDiscoveryProgress
    {
        public string Message { get; set; }
        public int DevicesFound { get; set; }
        public int DevicesResolved { get; set; }
        public int DevicesTotal { get; set; }
    }

    public sealed class OnvifStreamProfile
    {
        public string Token { get; set; }
        public string Name { get; set; }
        public string Encoding { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public double FrameRate { get; set; }
        public string StreamUri { get; set; }
    }

    public sealed class OnvifDevice
    {
        public string EndpointReference { get; set; }
        public string DeviceServiceUri { get; set; }
        public string MediaServiceUri { get; set; }
        public string Name { get; set; }
        public string Manufacturer { get; set; }
        public string Model { get; set; }
        public string FirmwareVersion { get; set; }
        public string SerialNumber { get; set; }
        public string HardwareId { get; set; }
        public string ErrorMessage { get; set; }
        public List<string> XAddresses { get; set; }
        public List<string> Scopes { get; set; }
        public List<OnvifStreamProfile> Profiles { get; set; }

        /// <summary>初始化 ONVIF 设备的地址、作用域和媒体配置集合。</summary>
        public OnvifDevice()
        {
            XAddresses = new List<string>();
            Scopes = new List<string>();
            Profiles = new List<OnvifStreamProfile>();
        }
    }

    internal sealed class OnvifProbeMatch
    {
        public string EndpointReference { get; set; }
        public List<string> XAddresses { get; private set; }
        public List<string> Scopes { get; private set; }

        /// <summary>初始化 WS-Discovery 探测响应的地址和作用域集合。</summary>
        public OnvifProbeMatch()
        {
            XAddresses = new List<string>();
            Scopes = new List<string>();
        }
    }

    public static class OnvifDiscoveryService
    {
        private const string SoapNamespace = "http://www.w3.org/2003/05/soap-envelope";
        private const string AddressingNamespace = "http://schemas.xmlsoap.org/ws/2004/08/addressing";
        private const string DiscoveryNamespace = "http://schemas.xmlsoap.org/ws/2005/04/discovery";
        private const string DeviceNamespace = "http://www.onvif.org/ver10/device/wsdl";
        private const string MediaNamespace = "http://www.onvif.org/ver10/media/wsdl";
        private const string SecurityNamespace = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd";
        private const string UtilityNamespace = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd";
        private const string PasswordDigestType = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordDigest";
        private const string Base64EncodingType = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-soap-message-security-1.0#Base64Binary";
        private static readonly IPEndPoint DiscoveryEndpoint =
            new IPEndPoint(IPAddress.Parse("239.255.255.250"), 3702);
        private const int SoapTimeoutMilliseconds = 5000;

        /// <summary>通过 WS-Discovery 发现 ONVIF 设备并解析其媒体服务。</summary>
        public static async Task<IList<OnvifDevice>> DiscoverAsync(
            OnvifCredentials credentials,
            IProgress<OnvifDiscoveryProgress> progress,
            CancellationToken cancellationToken)
        {
            Report(progress, "正在通过 WS-Discovery 查找 ONVIF 设备...", 0, 0, 0);
            IList<OnvifProbeMatch> matches = await DiscoverProbeMatchesAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (matches.Count == 0)
            {
                Report(progress, "WS-Discovery 未收到 ONVIF 响应", 0, 0, 0);
                return new List<OnvifDevice>();
            }

            Report(progress, "发现 " + matches.Count + " 台 ONVIF 设备，正在解析媒体服务...",
                matches.Count, 0, matches.Count);

            List<OnvifDevice> devices = new List<OnvifDevice>();
            object resultLock = new object();
            int completed = 0;
            SemaphoreSlim gate = new SemaphoreSlim(6, 6);
            try
            {
                List<Task> tasks = new List<Task>();
                foreach (OnvifProbeMatch match in matches)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    OnvifProbeMatch captured = match;
                    tasks.Add(Task.Run(async () =>
                    {
                        await gate.WaitAsync(cancellationToken);
                        try
                        {
                            OnvifDevice device = await ResolveDeviceAsync(captured, credentials, cancellationToken);
                            lock (resultLock) devices.Add(device);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            OnvifDevice device = CreateDevice(captured);
                            device.ErrorMessage = ex.Message;
                            lock (resultLock) devices.Add(device);
                            AppLog.Warning("解析 ONVIF 设备 " + DeviceHost(device) + " 失败：" + ex.Message);
                        }
                        finally
                        {
                            gate.Release();
                            int done = Interlocked.Increment(ref completed);
                            Report(progress, "正在解析 ONVIF 设备 " + done + "/" + matches.Count,
                                matches.Count, done, matches.Count);
                        }
                    }, cancellationToken));
                }
                await Task.WhenAll(tasks);
            }
            finally
            {
                gate.Dispose();
            }

            return devices
                .OrderBy(DeviceHost, StringComparer.OrdinalIgnoreCase)
                .ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        /// <summary>在所有可用网卡上发送探测并汇总去重后的响应。</summary>
        private static async Task<IList<OnvifProbeMatch>> DiscoverProbeMatchesAsync(
            CancellationToken cancellationToken)
        {
            List<NetworkInterface> interfaces = GetDiscoveryInterfaces();
            if (interfaces.Count == 0)
                interfaces.Add(null);

            Dictionary<string, OnvifProbeMatch> matches =
                new Dictionary<string, OnvifProbeMatch>(StringComparer.OrdinalIgnoreCase);
            object matchesLock = new object();
            List<Task> tasks = new List<Task>();

            foreach (NetworkInterface network in interfaces)
            {
                NetworkInterface captured = network;
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        await ProbeInterfaceAsync(captured, matches, matchesLock, cancellationToken);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        AppLog.Warning("ONVIF 发现接口 " +
                            (captured == null ? "默认" : captured.Name) + " 失败：" + ex.Message);
                    }
                }, cancellationToken));
            }

            await Task.WhenAll(tasks);
            lock (matchesLock)
                return matches.Values.ToList();
        }

        /// <summary>从指定网卡发送 WS-Discovery 探测并接收设备响应。</summary>
        private static async Task ProbeInterfaceAsync(
            NetworkInterface network,
            IDictionary<string, OnvifProbeMatch> matches,
            object matchesLock,
            CancellationToken cancellationToken)
        {
            using (UdpClient client = new UdpClient(AddressFamily.InterNetwork))
            {
                client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                client.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
                client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
                if (network != null)
                {
                    UnicastIPAddressInformation localAddress = network.GetIPProperties()
                        .UnicastAddresses.FirstOrDefault(a =>
                            a.Address.AddressFamily == AddressFamily.InterNetwork &&
                            !IPAddress.IsLoopback(a.Address));
                    if (localAddress != null)
                    {
                        client.Client.SetSocketOption(SocketOptionLevel.IP,
                            SocketOptionName.MulticastInterface,
                            localAddress.Address.GetAddressBytes());
                    }
                }

                byte[] probe = Encoding.UTF8.GetBytes(BuildProbeMessage());
                await client.SendAsync(probe, probe.Length, DiscoveryEndpoint);
                await client.SendAsync(probe, probe.Length, DiscoveryEndpoint);

                DateTime deadline = DateTime.UtcNow.AddMilliseconds(3800);
                while (!cancellationToken.IsCancellationRequested && DateTime.UtcNow < deadline)
                {
                    int remaining = Math.Max(1, (int)(deadline - DateTime.UtcNow).TotalMilliseconds);
                    Task<UdpReceiveResult> receiveTask = client.ReceiveAsync();
                    Task delayTask = Task.Delay(remaining, cancellationToken);
                    Task completed = await Task.WhenAny(receiveTask, delayTask);
                    if (completed != receiveTask)
                    {
                        client.Close();
                        try { await receiveTask; } catch { }
                        cancellationToken.ThrowIfCancellationRequested();
                        break;
                    }

                    UdpReceiveResult result = await receiveTask;
                    foreach (OnvifProbeMatch match in ParseProbeResponse(result.Buffer))
                    {
                        string key = !string.IsNullOrWhiteSpace(match.EndpointReference)
                            ? match.EndpointReference.Trim()
                            : match.XAddresses.FirstOrDefault();
                        if (string.IsNullOrWhiteSpace(key))
                            continue;

                        lock (matchesLock)
                        {
                            OnvifProbeMatch existing;
                            if (!matches.TryGetValue(key, out existing))
                            {
                                matches[key] = match;
                            }
                            else
                            {
                                MergeDistinct(existing.XAddresses, match.XAddresses);
                                MergeDistinct(existing.Scopes, match.Scopes);
                            }
                        }
                    }
                }
            }
        }

        /// <summary>解析设备的服务地址、设备信息和可用的 RTSP 码流。</summary>
        private static async Task<OnvifDevice> ResolveDeviceAsync(
            OnvifProbeMatch match,
            OnvifCredentials credentials,
            CancellationToken cancellationToken)
        {
            OnvifDevice device = CreateDevice(match);
            if (string.IsNullOrWhiteSpace(device.DeviceServiceUri))
                throw new InvalidOperationException("设备未提供可访问的 ONVIF 服务地址。");

            try
            {
                XDocument capabilities = await SendSoapAsync(
                    device.DeviceServiceUri,
                    "http://www.onvif.org/ver10/device/wsdl/GetCapabilities",
                    "<tds:GetCapabilities xmlns:tds=\"" + DeviceNamespace +
                    "\"><tds:Category>All</tds:Category></tds:GetCapabilities>",
                    credentials,
                    cancellationToken);
                device.MediaServiceUri = FindCapabilityAddress(capabilities, "Media");
                if (string.IsNullOrWhiteSpace(device.MediaServiceUri))
                {
                    capabilities = await SendSoapAsync(
                        device.DeviceServiceUri,
                        "http://www.onvif.org/ver10/device/wsdl/GetCapabilities",
                        "<tds:GetCapabilities xmlns:tds=\"" + DeviceNamespace +
                        "\"><tds:Category>Media</tds:Category></tds:GetCapabilities>",
                        credentials,
                        cancellationToken);
                    device.MediaServiceUri = FindCapabilityAddress(capabilities, "Media");
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppendError(device, "GetCapabilities: " + ex.Message);
                try
                {
                    XDocument mediaCapabilities = await SendSoapAsync(
                        device.DeviceServiceUri,
                        "http://www.onvif.org/ver10/device/wsdl/GetCapabilities",
                        "<tds:GetCapabilities xmlns:tds=\"" + DeviceNamespace +
                        "\"><tds:Category>Media</tds:Category></tds:GetCapabilities>",
                        credentials,
                        cancellationToken);
                    device.MediaServiceUri = FindCapabilityAddress(mediaCapabilities, "Media");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception mediaEx)
                {
                    AppendError(device, "GetCapabilities(Media): " + mediaEx.Message);
                }
            }

            if (string.IsNullOrWhiteSpace(device.MediaServiceUri))
            {
                try
                {
                    XDocument services = await SendSoapAsync(
                        device.DeviceServiceUri,
                        "http://www.onvif.org/ver10/device/wsdl/GetServices",
                        "<tds:GetServices xmlns:tds=\"" + DeviceNamespace +
                        "\"><tds:IncludeCapability>false</tds:IncludeCapability></tds:GetServices>",
                        credentials,
                        cancellationToken);
                    device.MediaServiceUri = FindServiceAddress(services, MediaNamespace);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    AppendError(device, "GetServices: " + ex.Message);
                }
            }

            if (string.IsNullOrWhiteSpace(device.MediaServiceUri))
                device.MediaServiceUri = InferMediaServiceAddress(device.DeviceServiceUri);

            try
            {
                XDocument information = await SendSoapAsync(
                    device.DeviceServiceUri,
                    "http://www.onvif.org/ver10/device/wsdl/GetDeviceInformation",
                    "<tds:GetDeviceInformation xmlns:tds=\"" + DeviceNamespace + "\" />",
                    credentials,
                    cancellationToken);
                ApplyDeviceInformation(device, information);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppendError(device, "GetDeviceInformation: " + ex.Message);
            }

            if (string.IsNullOrWhiteSpace(device.MediaServiceUri))
            {
                AppendError(device, "未找到 ONVIF Media 服务。");
                return device;
            }

            try
            {
                XDocument profiles = await SendSoapAsync(
                    device.MediaServiceUri,
                    "http://www.onvif.org/ver10/media/wsdl/GetProfiles",
                    "<trt:GetProfiles xmlns:trt=\"" + MediaNamespace + "\" />",
                    credentials,
                    cancellationToken);
                device.Profiles = ParseProfiles(profiles);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppendError(device, "GetProfiles: " + ex.Message);
                return device;
            }

            foreach (OnvifStreamProfile profile in device.Profiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    string body =
                        "<trt:GetStreamUri xmlns:trt=\"" + MediaNamespace + "\">" +
                        "<trt:StreamSetup>" +
                        "<trt:Stream>RTP-Unicast</trt:Stream>" +
                        "<trt:Transport><trt:Protocol>RTSP</trt:Protocol></trt:Transport>" +
                        "</trt:StreamSetup>" +
                        "<trt:ProfileToken>" + SecurityElement.Escape(profile.Token) +
                        "</trt:ProfileToken></trt:GetStreamUri>";
                    XDocument stream = await SendSoapAsync(
                        device.MediaServiceUri,
                        "http://www.onvif.org/ver10/media/wsdl/GetStreamUri",
                        body,
                        credentials,
                        cancellationToken);
                    profile.StreamUri = FindElementValue(stream, "MediaUri", "Uri");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    AppendError(device, "GetStreamUri(" + profile.Token + "): " + ex.Message);
                }
            }

            if (!device.Profiles.Any(p => !string.IsNullOrWhiteSpace(p.StreamUri)))
                AppendError(device, "设备未返回可用的 RTSP 地址。");
            return device;
        }

        /// <summary>根据探测响应创建基础 ONVIF 设备对象。</summary>
        private static OnvifDevice CreateDevice(OnvifProbeMatch match)
        {
            return new OnvifDevice
            {
                EndpointReference = match.EndpointReference,
                XAddresses = new List<string>(match.XAddresses),
                Scopes = new List<string>(match.Scopes),
                Name = GetScopeValue(match.Scopes, "name"),
                DeviceServiceUri = match.XAddresses.FirstOrDefault(IsHttpUri)
            };
        }

        /// <summary>将 SOAP 响应中的设备基本信息写入设备对象。</summary>
        private static void ApplyDeviceInformation(OnvifDevice device, XDocument document)
        {
            XElement info = document.Descendants()
                .FirstOrDefault(e => IsElement(e, "GetDeviceInformationResponse"));
            if (info == null)
                info = document.Root;

            device.Manufacturer = ChildValue(info, "Manufacturer") ?? device.Manufacturer;
            device.Model = ChildValue(info, "Model") ?? device.Model;
            device.FirmwareVersion = ChildValue(info, "FirmwareVersion") ?? device.FirmwareVersion;
            device.SerialNumber = ChildValue(info, "SerialNumber") ?? device.SerialNumber;
            device.HardwareId = ChildValue(info, "HardwareId") ?? device.HardwareId;
            if (string.IsNullOrWhiteSpace(device.Name))
                device.Name = GetScopeValue(device.Scopes, "name") ??
                    GetScopeValue(device.Scopes, "hardware");
        }

        /// <summary>解析媒体配置列表及其分辨率、编码和帧率信息。</summary>
        private static List<OnvifStreamProfile> ParseProfiles(XDocument document)
        {
            List<OnvifStreamProfile> result = new List<OnvifStreamProfile>();
            foreach (XElement element in document.Descendants().Where(e =>
                IsElement(e, "Profiles") || IsElement(e, "Profile")))
            {
                string token = AttributeValue(element, "token");
                if (string.IsNullOrWhiteSpace(token) &&
                    !IsElement(element.Parent, "Profiles"))
                    continue;
                OnvifStreamProfile profile = new OnvifStreamProfile
                {
                    Token = token,
                    Name = ChildOrDescendantValue(element, "Name"),
                    Encoding = ChildOrDescendantValue(element, "Encoding")
                };

                XElement resolution = element.Descendants()
                    .FirstOrDefault(e => IsElement(e, "Resolution"));
                if (resolution != null)
                {
                    int value;
                    if (int.TryParse(ChildValue(resolution, "Width"), out value)) profile.Width = value;
                    if (int.TryParse(ChildValue(resolution, "Height"), out value)) profile.Height = value;
                }

                double frameRate;
                string rateText = ChildOrDescendantValue(element, "FrameRateLimit") ??
                    ChildOrDescendantValue(element, "FrameRate");
                if (double.TryParse(rateText, NumberStyles.Float, CultureInfo.InvariantCulture, out frameRate))
                    profile.FrameRate = frameRate;

                if (string.IsNullOrWhiteSpace(profile.Name))
                    profile.Name = string.IsNullOrWhiteSpace(profile.Token) ? "默认码流" : profile.Token;
                if (!string.IsNullOrWhiteSpace(profile.Token) &&
                    !result.Any(p => string.Equals(p.Token, profile.Token,
                        StringComparison.OrdinalIgnoreCase)))
                    result.Add(profile);
            }
            return result;
        }

        /// <summary>从服务能力响应中查找指定能力的访问地址。</summary>
        private static string FindCapabilityAddress(XDocument document, string capabilityName)
        {
            XElement capability = document.Descendants()
                .FirstOrDefault(e => IsElement(e, capabilityName));
            return capability == null ? null : ChildValue(capability, "XAddr");
        }

        /// <summary>从服务列表响应中查找指定命名空间的访问地址。</summary>
        private static string FindServiceAddress(XDocument document, string serviceNamespace)
        {
            foreach (XElement element in document.Descendants().Where(e => IsElement(e, "Service")))
            {
                string value = ChildValue(element, "Namespace");
                if (!string.Equals(value, serviceNamespace, StringComparison.OrdinalIgnoreCase))
                    continue;
                string address = ChildValue(element, "XAddr");
                if (!string.IsNullOrWhiteSpace(address))
                    return address;
            }
            return null;
        }

        /// <summary>查找指定父元素下的直接子元素文本值。</summary>
        private static string FindElementValue(XDocument document, string parentName, string childName)
        {
            XElement parent = document.Descendants()
                .FirstOrDefault(e => IsElement(e, parentName));
            return parent == null ? null : ChildValue(parent, childName);
        }

        /// <summary>根据标准设备服务路径推断媒体服务地址。</summary>
        private static string InferMediaServiceAddress(string deviceServiceUri)
        {
            Uri uri;
            if (!Uri.TryCreate(deviceServiceUri, UriKind.Absolute, out uri) ||
                uri.AbsolutePath.IndexOf("/onvif/device_service",
                    StringComparison.OrdinalIgnoreCase) < 0)
                return null;

            UriBuilder builder = new UriBuilder(uri)
            {
                Path = "/onvif/media_service",
                Query = string.Empty,
                Fragment = string.Empty
            };
            return builder.Uri.AbsoluteUri;
        }

        /// <summary>使用可选 WS-Security 凭据发送 ONVIF SOAP 请求。</summary>
        private static async Task<XDocument> SendSoapAsync(
            string endpoint,
            string action,
            string body,
            OnvifCredentials credentials,
            CancellationToken cancellationToken)
        {
            Uri uri;
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                throw new InvalidOperationException("无效的 ONVIF 服务地址。");

            string envelope =
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                "<s:Envelope xmlns:s=\"" + SoapNamespace + "\">" +
                BuildSecurityHeader(credentials) +
                "<s:Body>" + body + "</s:Body></s:Envelope>";
            byte[] payload = Encoding.UTF8.GetBytes(envelope);

            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(uri);
            request.Method = "POST";
            request.ContentType = "application/soap+xml; charset=utf-8; action=\"" + action + "\"";
            request.Accept = "application/soap+xml, text/xml";
            request.UserAgent = "ReCord-ONVIF/1.0";
            request.KeepAlive = false;
            request.Timeout = SoapTimeoutMilliseconds;
            request.ReadWriteTimeout = SoapTimeoutMilliseconds;
            request.ServicePoint.Expect100Continue = false;
            request.ContentLength = payload.Length;
            if (credentials != null && credentials.HasUsername)
                request.Credentials = new NetworkCredential(credentials.Username, credentials.Password);

            CancellationTokenRegistration registration = cancellationToken.Register(AbortRequest, request);
            try
            {
                using (Stream stream = await request.GetRequestStreamAsync())
                    await stream.WriteAsync(payload, 0, payload.Length);

                using (WebResponse response = await request.GetResponseAsync())
                    return await ReadSoapDocumentAsync(response, cancellationToken);
            }
            catch (WebException ex)
            {
                if (cancellationToken.IsCancellationRequested)
                    throw new OperationCanceledException(cancellationToken);
                throw new InvalidOperationException(BuildHttpError(ex, credentials), ex);
            }
            finally
            {
                registration.Dispose();
            }
        }

        /// <summary>在超时控制下读取并解析 ONVIF SOAP 响应。</summary>
        private static async Task<XDocument> ReadSoapDocumentAsync(
            WebResponse response,
            CancellationToken cancellationToken)
        {
            using (Stream stream = response.GetResponseStream())
            {
                if (stream == null)
                    throw new InvalidOperationException("ONVIF 服务返回了空响应。");
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true))
                {
                    Task<string> readTask = reader.ReadToEndAsync();
                    Task completed = await Task.WhenAny(
                        readTask, Task.Delay(SoapTimeoutMilliseconds, cancellationToken));
                    if (completed != readTask)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        throw new TimeoutException("读取 ONVIF 响应超时。");
                    }

                    string text = await readTask;
                    if (string.IsNullOrWhiteSpace(text))
                        throw new InvalidOperationException("ONVIF 服务返回了空响应。");
                    XDocument document = XDocument.Parse(text, LoadOptions.None);
                    XElement fault = document.Descendants()
                        .FirstOrDefault(e => IsElement(e, "Fault"));
                    if (fault != null)
                    {
                        string reason = string.Join(" ", fault.Descendants()
                            .Where(e => IsElement(e, "Text") || IsElement(e, "faultstring"))
                            .Select(e => e.Value)
                            .Where(v => !string.IsNullOrWhiteSpace(v)));
                        throw new InvalidOperationException("ONVIF SOAP Fault: " +
                            (string.IsNullOrWhiteSpace(reason) ? fault.Value.Trim() : reason.Trim()));
                    }
                    return document;
                }
            }
        }

        /// <summary>生成带密码摘要、随机数和时间戳的 WS-Security 头。</summary>
        private static string BuildSecurityHeader(OnvifCredentials credentials)
        {
            if (credentials == null || !credentials.HasUsername)
                return string.Empty;

            byte[] nonceBytes = new byte[16];
            using (RandomNumberGenerator random = RandomNumberGenerator.Create())
                random.GetBytes(nonceBytes);
            string nonce = Convert.ToBase64String(nonceBytes);
            string created = DateTime.UtcNow.ToString(
                "yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

            byte[] nonceValue = Encoding.UTF8.GetBytes(nonce);
            byte[] createdValue = Encoding.UTF8.GetBytes(created);
            byte[] passwordValue = Encoding.UTF8.GetBytes(credentials.Password ?? string.Empty);
            byte[] digestInput = new byte[
                nonceValue.Length + createdValue.Length + passwordValue.Length];
            Buffer.BlockCopy(nonceValue, 0, digestInput, 0, nonceValue.Length);
            Buffer.BlockCopy(createdValue, 0, digestInput, nonceValue.Length, createdValue.Length);
            Buffer.BlockCopy(passwordValue, 0, digestInput,
                nonceValue.Length + createdValue.Length, passwordValue.Length);

            string digest;
            using (SHA1 sha = SHA1.Create())
                digest = Convert.ToBase64String(sha.ComputeHash(digestInput));

            return "<s:Header><wsse:Security xmlns:wsse=\"" + SecurityNamespace +
                   "\" xmlns:wsu=\"" + UtilityNamespace + "\" s:mustUnderstand=\"1\">" +
                   "<wsse:UsernameToken>" +
                   "<wsse:Username>" + SecurityElement.Escape(credentials.Username) +
                   "</wsse:Username>" +
                   "<wsse:Password Type=\"" + PasswordDigestType + "\">" + digest +
                   "</wsse:Password>" +
                   "<wsse:Nonce EncodingType=\"" + Base64EncodingType + "\">" + nonce +
                   "</wsse:Nonce>" +
                   "<wsu:Created>" + created + "</wsu:Created>" +
                   "</wsse:UsernameToken></wsse:Security></s:Header>";
        }

        /// <summary>生成用于发现网络视频发送设备的 WS-Discovery 探测消息。</summary>
        private static string BuildProbeMessage()
        {
            string messageId = "urn:uuid:" + Guid.NewGuid().ToString("D");
            return "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                "<s:Envelope xmlns:s=\"" + SoapNamespace + "\" xmlns:a=\"" +
                AddressingNamespace + "\" xmlns:d=\"" + DiscoveryNamespace +
                "\" xmlns:dn=\"http://www.onvif.org/ver10/network/wsdl\">" +
                "<s:Header>" +
                "<a:Action s:mustUnderstand=\"1\">" +
                "http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe</a:Action>" +
                "<a:MessageID>" + messageId + "</a:MessageID>" +
                "<a:ReplyTo><a:Address>" +
                "http://schemas.xmlsoap.org/ws/2004/08/addressing/role/anonymous" +
                "</a:Address></a:ReplyTo>" +
                "<a:To s:mustUnderstand=\"1\">" +
                "urn:schemas-xmlsoap-org:ws:2005:04:discovery</a:To>" +
                "</s:Header><s:Body><d:Probe><d:Types>" +
                "dn:NetworkVideoTransmitter</d:Types></d:Probe></s:Body></s:Envelope>";
        }

        /// <summary>解析 UDP 探测响应中的设备地址和作用域信息。</summary>
        private static IEnumerable<OnvifProbeMatch> ParseProbeResponse(byte[] buffer)
        {
            if (buffer == null || buffer.Length == 0)
                yield break;

            XDocument document;
            try
            {
                int length = buffer.Length;
                while (length > 0 && buffer[length - 1] == 0)
                    length--;
                string text = Encoding.UTF8.GetString(buffer, 0, length);
                document = XDocument.Parse(text, LoadOptions.None);
            }
            catch
            {
                yield break;
            }

            foreach (XElement element in document.Descendants()
                .Where(e => e.Name.LocalName == "ProbeMatch"))
            {
                XElement endpoint = element.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "Address");
                string scopes = ChildValue(element, "Scopes");
                string addresses = ChildValue(element, "XAddrs");
                OnvifProbeMatch match = new OnvifProbeMatch
                {
                    EndpointReference = endpoint == null ? null : endpoint.Value.Trim()
                };
                if (!string.IsNullOrWhiteSpace(scopes))
                    match.Scopes.AddRange(scopes.Split(
                        new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
                if (!string.IsNullOrWhiteSpace(addresses))
                    match.XAddresses.AddRange(addresses.Split(
                        new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                        .Where(IsHttpUri));
                if (match.XAddresses.Count > 0 ||
                    !string.IsNullOrWhiteSpace(match.EndpointReference))
                    yield return match;
            }
        }

        /// <summary>获取具备活动 IPv4 地址且可用于设备发现的网卡。</summary>
        private static List<NetworkInterface> GetDiscoveryInterfaces()
        {
            List<NetworkInterface> result = new List<NetworkInterface>();
            try
            {
                foreach (NetworkInterface network in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (network.OperationalStatus != OperationalStatus.Up ||
                        network.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                        network.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                        continue;
                    if (!network.GetIPProperties().UnicastAddresses.Any(
                        a => a.Address.AddressFamily == AddressFamily.InterNetwork &&
                             !IPAddress.IsLoopback(a.Address)))
                        continue;
                    result.Add(network);
                }
            }
            catch (Exception ex)
            {
                AppLog.Warning("读取 ONVIF 发现网卡失败：" + ex.Message);
            }
            return result;
        }

        /// <summary>将 ONVIF 凭据嵌入 RTSP 流地址。</summary>
        public static string AddCredentialsToStreamUri(
            string streamUri,
            OnvifCredentials credentials)
        {
            if (string.IsNullOrWhiteSpace(streamUri) || credentials == null ||
                !credentials.HasUsername)
                return streamUri;
            Uri uri;
            if (!Uri.TryCreate(streamUri, UriKind.Absolute, out uri))
                return streamUri;
            try
            {
                return new UriBuilder(uri)
                {
                    UserName = credentials.Username,
                    Password = credentials.Password ?? string.Empty
                }.Uri.AbsoluteUri;
            }
            catch
            {
                return streamUri;
            }
        }

        /// <summary>从设备服务地址或备用地址中提取主机名。</summary>
        public static string DeviceHost(OnvifDevice device)
        {
            if (device == null)
                return string.Empty;
            Uri uri;
            if (Uri.TryCreate(device.DeviceServiceUri, UriKind.Absolute, out uri))
                return uri.Host;
            foreach (string address in device.XAddresses)
            {
                if (Uri.TryCreate(address, UriKind.Absolute, out uri))
                    return uri.Host;
            }
            return string.Empty;
        }

        /// <summary>从 ONVIF 作用域列表中提取指定键的值。</summary>
        private static string GetScopeValue(IEnumerable<string> scopes, string key)
        {
            if (scopes == null)
                return null;
            string marker = "/" + key + "/";
            foreach (string scope in scopes)
            {
                int index = scope.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (index < 0)
                    continue;
                string value = scope.Substring(index + marker.Length);
                int query = value.IndexOfAny(new[] { '?', '#' });
                if (query >= 0)
                    value = value.Substring(0, query);
                try { return Uri.UnescapeDataString(value).Trim(); }
                catch { return value.Trim(); }
            }
            return null;
        }

        /// <summary>判断文本是否为有效的 HTTP 或 HTTPS 地址。</summary>
        private static bool IsHttpUri(string value)
        {
            Uri uri;
            return Uri.TryCreate(value, UriKind.Absolute, out uri) &&
                   (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        }

        /// <summary>判断 XML 元素是否具有指定的本地名称。</summary>
        private static bool IsElement(XElement element, string localName)
        {
            return element != null && element.Name.LocalName == localName;
        }

        /// <summary>读取指定名称的直接子元素文本值。</summary>
        private static string ChildValue(XElement element, string localName)
        {
            if (element == null)
                return null;
            XElement child = element.Elements()
                .FirstOrDefault(e => IsElement(e, localName));
            return child == null ? null : child.Value.Trim();
        }

        /// <summary>读取指定名称的子元素或后代元素文本值。</summary>
        private static string ChildOrDescendantValue(XElement element, string localName)
        {
            if (element == null)
                return null;
            XElement child = element.Elements()
                .FirstOrDefault(e => IsElement(e, localName));
            if (child == null)
                child = element.Descendants()
                    .FirstOrDefault(e => IsElement(e, localName));
            return child == null ? null : child.Value.Trim();
        }

        /// <summary>读取 XML 元素中指定本地名称的属性值。</summary>
        private static string AttributeValue(XElement element, string localName)
        {
            XAttribute attribute = element == null
                ? null
                : element.Attributes().FirstOrDefault(a => a.Name.LocalName == localName);
            return attribute == null ? null : attribute.Value;
        }

        /// <summary>以忽略大小写的方式将非空且未出现的值合并到集合。</summary>
        private static void MergeDistinct(List<string> target, IEnumerable<string> values)
        {
            foreach (string value in values)
            {
                if (!string.IsNullOrWhiteSpace(value) &&
                    !target.Any(v => string.Equals(v, value, StringComparison.OrdinalIgnoreCase)))
                    target.Add(value);
            }
        }

        /// <summary>向调用方报告 ONVIF 设备发现进度。</summary>
        private static void Report(
            IProgress<OnvifDiscoveryProgress> progress,
            string message,
            int found,
            int resolved,
            int total)
        {
            if (progress == null)
                return;
            progress.Report(new OnvifDiscoveryProgress
            {
                Message = message,
                DevicesFound = found,
                DevicesResolved = resolved,
                DevicesTotal = total
            });
        }

        /// <summary>将解析错误追加到设备的错误消息中。</summary>
        private static void AppendError(OnvifDevice device, string message)
        {
            if (device == null || string.IsNullOrWhiteSpace(message))
                return;
            device.ErrorMessage = string.IsNullOrWhiteSpace(device.ErrorMessage)
                ? message
                : device.ErrorMessage + "；" + message;
        }

        /// <summary>将 ONVIF HTTP 或网络异常转换为用户可读消息。</summary>
        private static string BuildHttpError(
            WebException exception,
            OnvifCredentials credentials)
        {
            HttpWebResponse response = exception.Response as HttpWebResponse;
            if (response != null)
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                    return credentials == null || !credentials.HasUsername
                        ? "ONVIF 接口需要用户名和密码。"
                        : "ONVIF 认证失败，请检查用户名和密码。";
                return "ONVIF 服务返回 HTTP " + (int)response.StatusCode + " " +
                       response.StatusDescription + "。";
            }
            if (exception.Status == WebExceptionStatus.Timeout)
                return "连接 ONVIF 服务超时。";
            return "连接 ONVIF 服务失败：" + exception.Message;
        }

        /// <summary>取消令牌触发时中止正在执行的 ONVIF 请求。</summary>
        private static void AbortRequest(object state)
        {
            HttpWebRequest request = state as HttpWebRequest;
            if (request == null)
                return;
            try { request.Abort(); } catch { }
        }
    }
}
