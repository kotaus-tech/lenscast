using System.Buffers.Binary;
using System.Text;
using Lenscast;
static void Check(bool condition,string name) { if(!condition) throw new Exception(name); Console.WriteLine("Пройдено: " + name); }
var payload=Encoding.UTF8.GetBytes("{\"type\":\"ping\",\"sent\":123}");
var bytes=new byte[4+payload.Length]; BinaryPrimitives.WriteInt32BigEndian(bytes,payload.Length); payload.CopyTo(bytes,4);
var message=await Connection.Read(new MemoryStream(bytes),CancellationToken.None);
Check(message.GetProperty("sent").GetInt32()==123,"Чтение сообщения управления");
foreach(var length in new[]{-1,0,1,65537,int.MaxValue}) {
 var header=new byte[4]; BinaryPrimitives.WriteInt32BigEndian(header,length);
 try { await Connection.Read(new MemoryStream(header),CancellationToken.None); throw new Exception("Длина принята"); } catch(InvalidDataException) {}
}
Check(true,"Недопустимые длины отклонены");
try { await Connection.Read(new MemoryStream(bytes[..^1]),CancellationToken.None); throw new Exception("Обрыв принят"); } catch(EndOfStreamException) {}
Check(true,"Частичное сообщение не принимается");
var names=new Dictionary<string,string>(); var hosts=new Dictionary<string,string>();
for(int n=0;n<1000;n++) { var malformed=new byte[Random.Shared.Next(0,300)]; Random.Shared.NextBytes(malformed); Discovery.Parse(malformed,names,hosts); }
Check(true,"Некорректные DNS-пакеты не приводят к сбою");
TrustStore.Save(new("192.0.2.1","TEST","secret"));
Check(TrustStore.Load("192.0.2.1")?.Token=="secret","Хранилище Windows DPAPI");
TrustStore.Forget(); Check(TrustStore.Load()==null,"Удаление доверия");

var clock = new ClockSync();
clock.Observe(1000, 1100, 2050);
Check(clock.BestRttUs == 100 && clock.PhoneMinusPcUs == 1000, "Синхронизация по середине RTT");
clock.Observe(2000, 2400, 2300);
Check(clock.PhoneMinusPcUs == 1000, "Медленные пробы не портят оценку часов");
clock.Observe(2000, 1999, 0);
Check(clock.BestRttUs == 100, "Некорректная метка времени отклонена");
TrustStore.Save(new("192.0.2.1", "CERT1", "token1"));
TrustStore.Save(new("192.0.2.2", "CERT2", "token2"));
Check(TrustStore.ForFingerprint("CERT1")?.Token == "token1", "Два сопряжённых телефона сохраняются независимо");
TrustStore.Save(TrustStore.ForFingerprint("CERT1")! with { Address="192.0.2.3" });
Check(TrustStore.Load("192.0.2.1") is null && TrustStore.Load("192.0.2.3")?.Token == "token1", "Смена адреса не меняет доверие сертификату");
TrustStore.Forget("192.0.2.3");
Check(TrustStore.Load("192.0.2.3") is null && TrustStore.Load("192.0.2.2")?.Token=="token2", "Удаление одного сопряжения не затрагивает второе");
TrustStore.Forget();

// Real TCP sessions, fragmented messages, heartbeat and reconnect, no Android hardware.
using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(20));
var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 27185);
listener.Start();
using var connection = new Connection();
var twice = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
int samples = 0;
connection.Telemetry += _ => { if (Interlocked.Increment(ref samples) >= 2) twice.TrySetResult(); };
async Task Emit(Stream output, object value) {
 var data = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value);
 var header = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(header, data.Length);
 foreach(var b in header) await output.WriteAsync(new byte[] {b}, limit.Token);
 await output.WriteAsync(data, limit.Token); await output.FlushAsync(limit.Token);
}
var phone = Task.Run(async () => {
 for(int attempt=0; attempt<2; attempt++) {
  using var tcp = await listener.AcceptTcpClientAsync(limit.Token);
  tcp.NoDelay = true; using var stream = tcp.GetStream();
  var hello = await Connection.Read(stream, limit.Token);
  Check(hello.GetProperty("type").GetString()=="hello", "Начало нового сеанса TCP");
  await Emit(stream, new { type="ready",version=1,name="Проверка",mode="Экран" });
  var ping = await Connection.Read(stream, limit.Token);
  Check(ping.GetProperty("type").GetString()=="ping", "Проверка активности ПК");
  await Emit(stream, new { type="pong",sent=ping.GetProperty("sent").GetInt64(),phoneUs=10000L });
  await Emit(stream, new { type="telemetry",battery=70 });
  if(attempt==0) continue; // Simulate a brief link interruption.
  var stop = await Connection.Read(stream, limit.Token);
  Check(stop.GetProperty("type").GetString()=="stop", "Команда остановки после переподключения");
 }
}, limit.Token);
try {
 connection.Start("127.0.0.1", "", true);
 await twice.Task.WaitAsync(limit.Token);
 await connection.Send(new {type="stop"}, limit.Token);
 await phone;
 Check(samples==2, "Телеметрия возобновляется после обрыва TCP");
} finally { connection.Dispose(); listener.Stop(); }

// Authoritative DNS-SD response with separate PTR, SRV and A records.
var dns = new List<byte>(new byte[12]); dns[2]=0x84; dns[7]=3;
void DnsName(string value) { foreach(var label in value.Split('.')) { var b=Encoding.UTF8.GetBytes(label); dns.Add((byte)b.Length); dns.AddRange(b); } dns.Add(0); }
void U16(int value) { dns.Add((byte)(value>>8)); dns.Add((byte)value); }
void Record(string owner,int type,Action write) {
 DnsName(owner); U16(type); U16(1); dns.AddRange(new byte[]{0,0,0,10}); var lengthPosition=dns.Count; U16(0);
 var start=dns.Count; write(); var length=dns.Count-start; dns[lengthPosition]=(byte)(length>>8); dns[lengthPosition+1]=(byte)length;
}
Record("_lenscast._tcp.local",12,()=>DnsName("Телефон._lenscast._tcp.local"));
Record("Телефон._lenscast._tcp.local",33,()=>{ U16(0); U16(0); U16(27183); DnsName("android.local"); });
Record("android.local",1,()=>dns.AddRange(new byte[]{192,0,2,7}));
names.Clear(); hosts.Clear(); Discovery.Parse(dns.ToArray(),names,hosts);
Check(names["Телефон._lenscast._tcp.local"]=="android.local" && hosts["android.local"]=="192.0.2.7","DNS-SD PTR/SRV/A и русское имя");
Check(Discovery.Query("_lenscast._tcp.local",12)[5]==1,"Формат запроса DNS-SD");
