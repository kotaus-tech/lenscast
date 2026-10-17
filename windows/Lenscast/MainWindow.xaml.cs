using System.Net;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
namespace Lenscast;
public partial class MainWindow : Wpf.Ui.Controls.FluentWindow {
 private readonly Connection connection=new();
 private bool usb;
 public MainWindow() {
  InitializeComponent();
  connection.Status += s=>Dispatcher.InvokeAsync(()=>Status.Text=s);
  connection.Telemetry += t=>Dispatcher.InvokeAsync(()=>ShowTelemetry(t));
  Closed += (_,_)=>connection.Dispose();
  Address.Text=TrustStore.Load()?.Address ?? "";
  Log.Write("Lenscast запущен");
 }
 private void ShowTelemetry(JsonElement t) {
  var text=$"Заряд {t.GetProperty("battery").GetInt32()}%{(t.GetProperty("charging").GetBoolean()?" · заряжается":"")}   ·   {t.GetProperty("temperature").GetDouble():F1} °C   ·   {t.GetProperty("network").GetString()}";
  if(t.GetProperty("network").GetString()!.Contains("2,4")) text+="\nДля высокого качества подключите телефон к Wi-Fi 5 ГГц.";
  if(t.GetProperty("thermal").GetInt32()>=3) text+="\nТелефон нагревается. Дайте ему остыть.";
  ScreenTelemetry.Text=CameraTelemetry.Text=text;
 }
 private async void Discover_Click(object sender,RoutedEventArgs e) { await Find(false); }
 private async void Usb_Click(object sender,RoutedEventArgs e) { await Find(true); }
 private async Task Find(bool useUsb) {
  Status.Text="Ищем телефоны…";
  try { var phones=useUsb?await Discovery.Usb():await Discovery.Network(); Devices.ItemsSource=phones; Status.Text=phones.Count>0?"Выберите телефон из списка.":"Телефон не найден. Проверьте «Старт» на телефоне или введите его адрес."; }
  catch(Exception ex) { Log.Write("Не удалось найти телефон",ex); Status.Text=useUsb?ex.Message:"Поиск в сети недоступен. Введите адрес телефона вручную."; }
 }
 private void Devices_SelectionChanged(object sender,SelectionChangedEventArgs e) { if(Devices.SelectedItem is Phone p) { Address.Text=p.Address; usb=p.Usb; } }
 private void Connect_Click(object sender,RoutedEventArgs e) {
  if(!IPAddress.TryParse(Address.Text.Trim(),out var ip) || ip.AddressFamily!=System.Net.Sockets.AddressFamily.InterNetwork) { Status.Text="Введите IPv4-адрес телефона из настроек Wi-Fi."; return; }
  if(IPAddress.IsLoopback(ip) && !usb) { Status.Text="Для USB нажмите «Проверить USB» и выберите телефон."; return; }
  connection.Start(ip.ToString(),Code.Text.Trim(),usb && IPAddress.IsLoopback(ip)); Code.Clear();
 }
 private void Disconnect_Click(object sender,RoutedEventArgs e) { connection.Dispose(); Status.Text="Отключено"; }
 private async void Stop_Click(object sender,RoutedEventArgs e) { try { await connection.Send(new {type="stop"}); connection.Dispose(); Status.Text="Связь остановлена на телефоне"; } catch(Exception ex) { Log.Write("Остановка не доставлена",ex); Status.Text="Не удалось остановить телефон. Нажмите «Стоп» на телефоне."; } }
 private async void Forget_Click(object sender,RoutedEventArgs e) { try { await connection.Send(new {type="forget"}); } catch(Exception ex) { Log.Write("Сброс доверия не доставлен",ex); } connection.Dispose(); TrustStore.Forget(); Status.Text="Телефон забыт. При следующем подключении введите код."; }
 private void Log_Click(object sender,RoutedEventArgs e) {
  var dialog=new SaveFileDialog {FileName="Lenscast-журнал.txt",Filter="Текстовый журнал (*.txt)|*.txt"};
  if(dialog.ShowDialog()!=true) return;
  try { Log.Write("Экспорт журнала"); System.IO.File.Copy(Log.FilePath,dialog.FileName,true); Status.Text="Журнал сохранён"; } catch(Exception ex) { Log.Write("Не удалось сохранить журнал",ex); Status.Text="Не удалось сохранить файл. Выберите другую папку."; }
 }
}
