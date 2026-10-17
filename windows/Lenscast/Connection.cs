using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
namespace Lenscast;
internal sealed class Connection : IDisposable {
 private CancellationTokenSource? stop;
 private Stream? stream;
 private readonly SemaphoreSlim sendLock = new(1,1);
 internal event Action<string>? Status;
 internal event Action<JsonElement>? Telemetry;
 private bool mayReconnect;
 internal void Start(string address, string code, bool usb) { Dispose(); stop = new(); mayReconnect = true; _ = Run(address, code, usb, stop.Token); }
 private async Task Run(string address, string code, bool usb, CancellationToken ct) {
  var first = true;
  while (!ct.IsCancellationRequested && mayReconnect) {
   try {
    Status?.Invoke(first ? "Подключаемся…" : "Связь прервана · повторное подключение…");
    using var client = new TcpClient { NoDelay = true };
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(10));
    await client.ConnectAsync(address, usb ? 27185 : 27183, timeout.Token);
    using var net = client.GetStream();
    var trust = usb ? null : TrustStore.Load(address);
    string fingerprint = "";
    Stream active = net;
    if (!usb) {
     var tls = new SslStream(net, false, (_, cert, _, _) => {
      if (cert is null) return false;
      fingerprint = Convert.ToHexString(SHA256.HashData(cert.GetRawCertData()));
      return trust is null || string.Equals(trust.Fingerprint, fingerprint, StringComparison.Ordinal);
     });
     active = tls;
     await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "Lenscast", EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13, CertificateRevocationCheckMode = X509RevocationMode.NoCheck }, timeout.Token);
    }
    using (active) {
     stream = active;
     await Send(new { type="hello", version=1, code=trust is null ? code : "", token=trust?.Token ?? "" }, ct);
     var reply = await Read(active, timeout.Token);
     if (reply.GetProperty("type").GetString() != "ready") { mayReconnect = false; Status?.Invoke(reply.GetProperty("message").GetString()!); return; }
     if (!usb && reply.TryGetProperty("token", out var token)) TrustStore.Save(new(address, fingerprint, token.GetString()!));
     first = false;
     Status?.Invoke("Подключён: " + reply.GetProperty("name").GetString() + (usb ? " · USB" : " · Wi-Fi"));
     Log.Write("Телефон подключён");
     long nextPing = 0;
     while (!ct.IsCancellationRequested) {
      if (Environment.TickCount64 >= nextPing) { await Send(new { type="ping", sent=Stopwatch.GetTimestamp() }, ct); nextPing=Environment.TickCount64+2000; }
      using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct); readTimeout.CancelAfter(TimeSpan.FromSeconds(6));
      var message = await Read(active, readTimeout.Token);
      if (message.GetProperty("type").GetString() == "telemetry") Telemetry?.Invoke(message);

     }
    }
   } catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
   catch (AuthenticationException e) { Log.Write("Сертификат не совпал или TLS недоступен",e); Status?.Invoke("Не удалось проверить телефон. Проверьте адрес; для нового телефона нажмите «Забыть телефон»."); break; }
   catch (Exception e) { Log.Write("Связь прервана", e); if (first) { Status?.Invoke("Телефон недоступен. Проверьте адрес и нажмите «Старт» на телефоне."); break; } }
   finally { stream = null; }
   try { await Task.Delay(1500, ct); } catch (OperationCanceledException) { break; }
  }
 }
 internal async Task Send(object message, CancellationToken ct = default) {
  await sendLock.WaitAsync(ct);
  try { var s = stream; if (s is null) return; var data=JsonSerializer.SerializeToUtf8Bytes(message); var header=new byte[4]; BinaryPrimitives.WriteInt32BigEndian(header, data.Length); await s.WriteAsync(header,ct); await s.WriteAsync(data,ct); await s.FlushAsync(ct); }
  finally { sendLock.Release(); }
 }
 internal static async Task<JsonElement> Read(Stream input, CancellationToken ct) {
  var header=new byte[4]; await input.ReadExactlyAsync(header,ct); int n=BinaryPrimitives.ReadInt32BigEndian(header);
  if (n < 2 || n > 65536) throw new InvalidDataException("Недопустимый размер сообщения");
  var data=new byte[n]; await input.ReadExactlyAsync(data,ct); using var doc=JsonDocument.Parse(data); return doc.RootElement.Clone();
 }
 public void Dispose() { stop?.Cancel(); stop?.Dispose(); stop=null; try { stream?.Dispose(); } catch (IOException) {} stream=null; }
}
