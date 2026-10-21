using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using VTD.Platform.Sources;

namespace VTD.Platform.Net;

/// <summary>
/// Optional network probes: one SNTP query to <c>time.windows.com</c> (what Windows itself syncs with) for clock skew,
/// and ICMP echoes. Nothing is sent unless the scan request allows network probes.
/// </summary>
/// <param name="clock">Local clock (the skew is local minus reference).</param>
public sealed class NetworkProbeSource(TimeProvider clock) : INetworkProbeSource
{
    private const string NtpHost = "time.windows.com";

    /// <inheritdoc />
    public ClockRaw MeasureClock(TimeSpan timeout)
    {
        try
        {
            var task = Task.Run(() => QueryNtp(timeout));
            if (!task.Wait(timeout + TimeSpan.FromSeconds(1)))
            {
                return new ClockRaw { Status = ReadStatus.TimedOut };
            }

            return task.Result;
        }
        catch (AggregateException)
        {
            return new ClockRaw { Status = ReadStatus.Failed };
        }
    }

    private ClockRaw QueryNtp(TimeSpan timeout)
    {
        try
        {
            var addresses = Dns.GetHostAddresses(NtpHost, AddressFamily.InterNetwork);
            if (addresses.Length == 0)
            {
                return new ClockRaw { Status = ReadStatus.Failed };
            }

            using var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.ReceiveTimeout = (int)timeout.TotalMilliseconds;
            udp.Connect(new IPEndPoint(addresses[0], 123));
            var request = new byte[48];
            request[0] = 0x1B; // LI = 0, VN = 3, Mode = 3 (client)
            var t1 = clock.GetUtcNow();
            udp.Send(request, request.Length);
            var remote = new IPEndPoint(IPAddress.Any, 0);
            var reply = udp.Receive(ref remote);
            var t4 = clock.GetUtcNow();
            if (reply.Length < 48)
            {
                return new ClockRaw { Status = ReadStatus.Failed };
            }

            var t2 = NtpTime(reply.AsSpan(32));
            var t3 = NtpTime(reply.AsSpan(40));
            var offset = ((t2 - t1) + (t3 - t4)) / 2; // reference minus local
            return new ClockRaw { Status = ReadStatus.Ok, SkewSeconds = (long)Math.Round(-offset.TotalSeconds), Source = "ntp" };
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
            return new ClockRaw { Status = ReadStatus.Failed };
        }
    }

    private static DateTimeOffset NtpTime(ReadOnlySpan<byte> b)
    {
        var seconds = BinaryPrimitives.ReadUInt32BigEndian(b);
        var fraction = BinaryPrimitives.ReadUInt32BigEndian(b[4..]);
        var epoch = new DateTimeOffset(1900, 1, 1, 0, 0, 0, TimeSpan.Zero);
        return epoch.AddSeconds(seconds).AddTicks((long)(fraction * (double)TimeSpan.TicksPerSecond / 4294967296.0));
    }

    /// <inheritdoc />
    public PingRaw Ping(string address, int count, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (!IPAddress.TryParse(address, out var ip))
        {
            return new PingRaw { Status = ReadStatus.Failed };
        }

        var rtts = new List<long>();
        try
        {
            using var ping = new Ping();
            for (var i = 0; i < count; i++)
            {
                var reply = ping.Send(ip, (int)timeout.TotalMilliseconds);
                if (reply.Status == IPStatus.Success)
                {
                    rtts.Add(reply.RoundtripTime);
                }
            }

            return new PingRaw { Status = ReadStatus.Ok, Sent = count, RttMs = rtts };
        }
        catch (Exception e) when (e is PingException or InvalidOperationException)
        {
            return new PingRaw { Status = ReadStatus.Failed, Sent = count, RttMs = rtts };
        }
    }
}
