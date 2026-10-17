using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
namespace Lenscast;

internal sealed class Connection : IDisposable {
 private sealed class Session {
  internal readonly CancellationTokenSource Stop = new();
  internal readonly SemaphoreSlim Writer = new(1, 1);
  internal Stream? Stream;
 }
 private Session? current;
 internal event Action<string>? Status;
 internal event Action<JsonElement>? Telemetry;
 internal event Action<double>? Roundtrip;
 internal void Start(string address, string code, bool usb) {
  Dispose();
  var session = new Session(); current = session;
  _ = Run(session, address, code, usb);
 }
 private void Report(Session s, string text) { if (ReferenceEquals(current, s) && !s.Stop.IsCancellationRequested) Status?.Invoke(text); }
 private async Task Run(Session session, string address, string code, bool usb) {
  var ct = session.Stop.Token;
  var first = true;
  try {
   while (!ct.IsCancellationRequested) {
    try {
     Report(session, first ? "Подключаемся…" : "Связь прервана · повторное подключение…");
     using var client = new TcpClient { NoDelay = true };
     using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
     timeout.CancelAfter(TimeSpan.FromSeconds(10));
     await client.ConnectAsync(address, usb ? 27185 : 27183, timeout.Token);
     using var net = client.GetStream();
     var trust = usb ? null : TrustStore.Load(address);
     string fingerprint = "";
     Stream active = net;
     if (!usb) {
      var tls = new SslStream(net, false, (_, cert, _, _) => {
       if (cert is null) return false;
       fingerprint = Convert.ToHexString(SHA256.HashData(cert.GetRawCertData()));
       // A known address must never silently accept a different certificate.
       if (trust is not null && trust.Fingerprint != fingerprint) return false;
       // The same certificate on a new DHCP address is still the paired phone.
       trust ??= TrustStore.ForFingerprint(fingerprint);
       return true;
      });
      active = tls;
      try {
       await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions {
        TargetHost = "Lenscast", EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        CertificateRevocationCheckMode = X509RevocationMode.NoCheck
       }, timeout.Token);
      } catch { tls.Dispose(); throw; }
     }
     using (active) {
      session.Stream = active;
      await Send(session, new { type="hello", version=1, code=trust is null ? code : "", token=trust?.Token ?? "" }, ct);
      var reply = await Read(active, timeout.Token);
      if (reply.GetProperty("type").GetString() != "ready") {
       Report(session, trust is not null && reply.GetProperty("type").GetString() == "pairing"
        ? "Доверие устарело. Нажмите «Забыть телефон» и подключитесь по коду."
        : reply.GetProperty("message").GetString()!);
       return;
      }
      if (!usb) {
       if (reply.TryGetProperty("token", out var token)) TrustStore.Save(new(address, fingerprint, token.GetString()!));
       else if (trust is not null) TrustStore.Save(trust with { Address=address });
      }
      first = false;
      Report(session, "Подключён: " + reply.GetProperty("name").GetString() + (usb ? " · USB" : " · Wi-Fi"));
      Log.Write("Телефон подключён");
      var clock = new ClockSync();
      long nextPing = 0;
      while (!ct.IsCancellationRequested) {
       if (Environment.TickCount64 >= nextPing) {
        await Send(session, new { type="ping", sent=PcUs() }, ct);
        nextPing = Environment.TickCount64 + 2000;
       }
       using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
       readTimeout.CancelAfter(TimeSpan.FromSeconds(6));
       var message = await Read(active, readTimeout.Token);
       if (!ReferenceEquals(current, session)) return;
       switch (message.GetProperty("type").GetString()) {
        case "telemetry": Telemetry?.Invoke(message); break;
        case "pong":
         clock.Observe(message.GetProperty("sent").GetInt64(), PcUs(), message.GetProperty("phoneUs").GetInt64());
         Roundtrip?.Invoke(clock.BestRttUs / 1000); break;
       }
      }
     }
    } catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
    catch (AuthenticationException e) {
     Log.Write("Сертификат не совпал или TLS недоступен", e);
     Report(session, "Не удалось проверить телефон. Проверьте адрес; для нового телефона нажмите «Забыть телефон».");
     break;
    }
    catch (Exception e) {
     Log.Write("Связь прервана", e);
     if (first) { Report(session, usb ? "Телефон найден, но Lenscast не отвечает. Нажмите «Старт» на телефоне и подключитесь снова." : "Телефон недоступен. Проверьте адрес и нажмите «Старт» на телефоне."); break; }
    }
    finally { session.Stream = null; }
    try { await Task.Delay(1500, ct); } catch (OperationCanceledException) { break; }
   }
  } finally { session.Stop.Dispose(); }
 }
 private static long PcUs() => (long)(Stopwatch.GetTimestamp() * (1_000_000.0 / Stopwatch.Frequency));
 internal Task Send(object message, CancellationToken ct = default) {
  var session = current ?? throw new IOException("Телефон не подключён");
  return Send(session, message, ct);
 }
 private static async Task Send(Session session, object message, CancellationToken ct) {
  using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
  timeout.CancelAfter(TimeSpan.FromSeconds(5));
  await session.Writer.WaitAsync(timeout.Token);
  try {
   var stream = session.Stream ?? throw new IOException("Телефон не подключён");
   var data = JsonSerializer.SerializeToUtf8Bytes(message);
   if (data.Length is < 2 or > 65536) throw new InvalidDataException("Недопустимый размер сообщения");
   var packet = new byte[4 + data.Length]; BinaryPrimitives.WriteInt32BigEndian(packet, data.Length); data.CopyTo(packet, 4);
   await stream.WriteAsync(packet, timeout.Token); await stream.FlushAsync(timeout.Token);
  } catch { session.Stream?.Dispose(); throw; }
  finally { session.Writer.Release(); }
 }
 internal static async Task<JsonElement> Read(Stream input, CancellationToken ct) {
  var header = new byte[4]; await input.ReadExactlyAsync(header, ct);
  int n = BinaryPrimitives.ReadInt32BigEndian(header);
  if (n < 2 || n > 65536) throw new InvalidDataException("Недопустимый размер сообщения");
  var data = new byte[n]; await input.ReadExactlyAsync(data, ct);
  using var doc = JsonDocument.Parse(data); return doc.RootElement.Clone();
 }
 public void Dispose() {
  var session = Interlocked.Exchange(ref current, null);
  if (session is null) return;
  try { session.Stop.Cancel(); } catch (ObjectDisposedException) { }
  try { session.Stream?.Dispose(); } catch (IOException) { }
 }
}
