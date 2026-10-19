using System.Net;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
namespace Lenscast;
public partial class MainWindow : Wpf.Ui.Controls.FluentWindow {
 private readonly Connection connection=new();
 private bool usb;
 private string? sessionAddress;
 private double? roundtrip;
 private readonly List<Phone> phones = [];
 private readonly DispatcherTimer discoveryTimer = new() { Interval = TimeSpan.FromSeconds(15) };
 private bool searching;
 private bool closing;
 private bool updatingDevices;
 private readonly CancellationTokenSource lifetime=new();
 private readonly SemaphoreSlim wifiSearch=new(1,1), usbSearch=new(1,1);
 public MainWindow() {
  InitializeComponent();
  gestureHeartbeat.Tick+=(_,_)=>{ if(pointerDown) Touch("move",lastX,lastY); }; gestureHeartbeat.Start();
  connection.LinkLost+=()=>Dispatcher.InvokeAsync(()=> {
   CameraLost(); canControl=false; CancelPointer(); Interlocked.Exchange(ref pendingFrame,null)?.Dispose(); Preview.Source=null; if(fullImage!=null) fullImage.Source=null; bitmap=null; VideoHint.Visibility=Visibility.Visible;
  });
  connection.Frame+=ReceiveFrame;
  connection.VideoStatus+=s=>Dispatcher.InvokeAsync(()=>ShowVideoStatus(s));
  Deactivated+=(_,_)=>CancelPointer();
  connection.Status += s=>Dispatcher.InvokeAsync(()=>Status.Text=s);
  connection.Roundtrip += ms=>Dispatcher.InvokeAsync(()=>roundtrip=ms);
  connection.Telemetry += t=>Dispatcher.InvokeAsync(()=>ShowTelemetry(t));
  Closed += (_,_)=>{ closing=true; gestureHeartbeat.Stop(); fullWindow?.Close(); ResetInput(); Interlocked.Exchange(ref pendingFrame,null)?.Dispose(); lifetime.Cancel(); discoveryTimer.Stop(); connection.Dispose(); };
  Loaded += async (_,_)=>{ if (!App.IsSmokeTest) { await AutoFind(); if (!closing) discoveryTimer.Start(); } };
  discoveryTimer.Tick += async (_,_)=>await AutoFind();
  Address.Text=TrustStore.Load()?.Address ?? "";
  VersionText.Text="Версия "+typeof(App).Assembly.GetName().Version?.ToString(3);
  Log.Write("Lenscast запущен · "+typeof(App).Assembly.GetName().Version);
 }
 private void ShowTelemetry(JsonElement t) {
  var text=$"Заряд {t.GetProperty("battery").GetInt32()}%{(t.GetProperty("charging").GetBoolean()?" · заряжается":"")}   ·   {t.GetProperty("temperature").GetDouble():F1} °C   ·   {t.GetProperty("network").GetString()}";
  if(t.TryGetProperty("fps",out var fps)) text+=$"   ·   передано {fps.GetInt32()} кадр/с · {t.GetProperty("bitrate").GetDouble()/1_000_000:F1} Мбит/с";
  if(t.GetProperty("network").GetString()!.Contains("2,4")) text+="\nДля высокого качества подключите телефон к Wi-Fi 5 ГГц.";
  if(t.GetProperty("thermal").GetInt32()>=3) text+="\nТелефон нагревается. Дайте ему остыть.";
  if(roundtrip is { } rtt) text+=$"   ·   отклик связи {rtt:F0} мс";
  canControl=t.TryGetProperty("screen",out var screen) && screen.GetBoolean() && t.TryGetProperty("gestures",out var gestures) && gestures.GetBoolean();
  ScreenTelemetry.Text=CameraTelemetry.Text=text;
  if(t.TryGetProperty("camera",out var camera) && connection.Mode=="Веб-камера") UpdateCamera(camera);
  if(t.TryGetProperty("screen",out var sharing) && !sharing.GetBoolean()) { Preview.Source=null; if(fullImage!=null) fullImage.Source=null; bitmap=null; VideoHint.Text="На телефоне нажмите «Разрешить показ экрана»"; VideoHint.Visibility=Visibility.Visible; }
 }
 private async void Discover_Click(object sender,RoutedEventArgs e) { await Find(false); }
 private async void Usb_Click(object sender,RoutedEventArgs e) { await Find(true); }
 private async Task AutoFind() {
  if (searching || closing) return;
  searching=true;
  try { await Task.WhenAll(Find(false,true),Find(true,true)); }
  finally { searching=false; }
 }
 private async Task Find(bool useUsb, bool silent=false) {
  var gate=useUsb?usbSearch:wifiSearch;
  if(closing || !await gate.WaitAsync(0,lifetime.Token)) return;
  var button=useUsb?FindUsb:FindWifi;
  var hint=useUsb?UsbHint:WifiHint;
  button.IsEnabled=false;
  hint.Text=useUsb?"USB: проверяем подключение…":"Wi-Fi: ищем телефоны…";
  if(!silent) Status.Text="Ищем телефоны…";
  try {
   var found=useUsb?await Discovery.Usb(lifetime.Token):await Discovery.Network(lifetime.Token);
   if(closing) return;
   ReplacePhones(useUsb,found);
   hint.Text=found.Count>0
    ?(useUsb?"USB: телефон найден. Нажмите «Старт» на телефоне и подключитесь.":"Wi-Fi: телефон найден. Выберите его и введите код.")
    :"Wi-Fi: телефон не найден. Попробуйте адрес с экрана телефона.";
   if(!silent) Status.Text=found.Count>0?"Выберите телефон из списка.":hint.Text;
  } catch(OperationCanceledException) when(lifetime.IsCancellationRequested) { }
  catch(Exception ex) {
   Log.Write(useUsb?"Ошибка поиска USB":"Ошибка поиска Wi-Fi",ex);
   if(closing) return;
   ReplacePhones(useUsb,[]);
   hint.Text=ex is UsbDiscoveryException ? ex.Message : useUsb
    ?"USB: проверка не удалась. Сохраните журнал в настройках."
    :"Wi-Fi: поиск недоступен. Введите адрес с экрана телефона.";
   if(!silent) Status.Text=hint.Text;
  } finally { if(!closing) button.IsEnabled=true; gate.Release(); }
 }
 private void ReplacePhones(bool useUsb,IEnumerable<Phone> found) {
  var selected=Devices.SelectedItem as Phone;
  updatingDevices=true;
  try {
   phones.RemoveAll(p=>p.Usb==useUsb); phones.AddRange(found);
   Devices.ItemsSource=phones.ToList();
   if(selected is not null) Devices.SelectedItem=phones.FirstOrDefault(p=>p.Address==selected.Address && p.Usb==selected.Usb);
  } finally { updatingDevices=false; }
 }
 private void Devices_SelectionChanged(object sender,SelectionChangedEventArgs e) { if(!updatingDevices && Devices.SelectedItem is Phone p) { Address.Text=p.Address; usb=p.Usb; } }
 private async void Connect_Click(object sender,RoutedEventArgs e) {
  if(!IPAddress.TryParse(Address.Text.Trim(),out var ip) || ip.AddressFamily!=System.Net.Sockets.AddressFamily.InterNetwork) { Status.Text="Введите IPv4-адрес телефона из настроек Wi-Fi."; return; }
  if(IPAddress.IsLoopback(ip) && !usb) { Status.Text="Для USB нажмите «Проверить USB» и выберите телефон."; return; }
  if (!string.IsNullOrEmpty(Code.Text) && (Code.Text.Length!=6 || !Code.Text.All(char.IsAsciiDigit))) { Status.Text="Введите шесть цифр с экрана телефона."; return; }
  var useUsb=usb && IPAddress.IsLoopback(ip);
  ConnectButton.IsEnabled=false;
  try {
   if(useUsb) {
    if(Devices.SelectedItem is not Phone { Usb:true,UsbSerial:not null } phone) { Status.Text="Выберите телефон USB из списка."; return; }
    Status.Text="Готовим подключение USB…";
    await UsbDiscovery.Forward(phone.UsbSerial,lifetime.Token);
   }
   CameraLost(); ResetInput(); canControl=false; roundtrip=null; sessionAddress=ip.ToString(); connection.Start(ip.ToString(),Code.Text.Trim(),useUsb); Code.Clear();
  } catch(OperationCanceledException) when(lifetime.IsCancellationRequested) { }
  catch(Exception ex) { Log.Write("Не удалось подготовить подключение",ex); Status.Text=ex is UsbDiscoveryException ? ex.Message : "Подключение не удалось. Сохраните журнал в настройках."; }
  finally { if(!closing) ConnectButton.IsEnabled=true; }
 }
 private void Disconnect_Click(object sender,RoutedEventArgs e) { CameraLost(); ResetInput(); canControl=false; connection.Dispose(); Preview.Source=null; if(fullImage!=null) fullImage.Source=null; bitmap=null; VideoHint.Visibility=Visibility.Visible; Status.Text="Отключено"; }
 private async void Stop_Click(object sender,RoutedEventArgs e) { CameraLost(); try { await connection.Send(new {type="stop"}); connection.Dispose(); Status.Text="Связь остановлена на телефоне"; } catch(Exception ex) { Log.Write("Остановка не доставлена",ex); Status.Text="Не удалось остановить телефон. Нажмите «Стоп» на телефоне."; } }
 private async void Forget_Click(object sender,RoutedEventArgs e) {
  var address=sessionAddress ?? Address.Text.Trim();
  if(!IPAddress.TryParse(address,out var ip)) { Status.Text="Выберите телефон, который нужно забыть."; return; }
  if(IPAddress.IsLoopback(ip)) { Status.Text="Для удаления сопряжения подключите телефон по Wi-Fi."; return; }
  try { await connection.Send(new {type="forget"}); } catch(Exception ex) { Log.Write("Сброс доверия не доставлен",ex); }
  CameraLost(); connection.Dispose(); TrustStore.Forget(address); sessionAddress=null;
  Status.Text="Телефон забыт. При следующем подключении введите код.";
 }
 private void Log_Click(object sender,RoutedEventArgs e) {
  var dialog=new SaveFileDialog {FileName="Lenscast-журнал.txt",Filter="Текстовый журнал (*.txt)|*.txt"};
  if(dialog.ShowDialog()!=true) return;
  try { Log.Write("Экспорт журнала"); System.IO.File.Copy(Log.FilePath,dialog.FileName,true); Status.Text="Журнал сохранён"; } catch(Exception ex) { Log.Write("Не удалось сохранить журнал",ex); Status.Text="Не удалось сохранить файл. Выберите другую папку."; }
 }
}
