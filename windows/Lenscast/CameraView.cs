using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace Lenscast;

public partial class MainWindow {
 private sealed record CameraChoice(string Id,string Label);
 private bool updatingCamera;
 private string cameraLayout="";
 private WriteableBitmap? cameraBitmap;
 private void CameraLost() { cameraLayout=""; CameraPreview.Source=null; cameraBitmap=null; CameraSelectors.IsEnabled=false; CameraStatus.Text="Связь прервана. Подключите телефон."; }
 private void PresentCameraFrame(VideoFrame f) {
  if(cameraBitmap is null || cameraBitmap.PixelWidth!=f.Width || cameraBitmap.PixelHeight!=f.Height) {
   cameraBitmap=new WriteableBitmap(f.Width,f.Height,96,96,PixelFormats.Bgra32,null); CameraPreview.Source=cameraBitmap;
  }
  cameraBitmap.WritePixels(new Int32Rect(0,0,f.Width,f.Height),f.Pixels,f.Width*4,0);
  CameraStats.Text=$"{f.Width}×{f.Height} · {f.Decoder}";
 }
 private void UpdateCamera(JsonElement state) {
  CameraStatus.Text=state.TryGetProperty("status",out var text)?text.GetString():"Запустите камеру на телефоне.";
  bool running=state.TryGetProperty("running",out var r) && r.GetBoolean();
  CameraSelectors.IsEnabled=running && state.TryGetProperty("modes",out _);
  if(!running) { CameraPreview.Source=null; cameraBitmap=null; }
  if(!state.TryGetProperty("cameraId",out var id)) return;
  if(state.TryGetProperty("actualIso",out var iso) && state.TryGetProperty("actualExposureMs",out var exposure)) CameraStatus.Text+=$" · ISO {iso.GetInt32()} · выдержка {exposure.GetDouble():F2} мс";
  if(state.TryGetProperty("actualFps",out var fps)) CameraStatus.Text+=$" · получено {fps.GetDouble():F1} кадр/с";
  if(state.TryGetProperty("ready",out var ready) && !ready.GetBoolean()) CameraStatus.Text+=" · режим ещё не подтверждён кадрами";
  int rotation=state.GetProperty("rotation").GetInt32(); bool mirror=state.GetProperty("mirror").GetBoolean();
  var transform=new TransformGroup(); transform.Children.Add(new ScaleTransform(mirror?-1:1,1)); transform.Children.Add(new RotateTransform(rotation)); CameraPreview.LayoutTransform=transform;
  var layout=id.GetString()+"/"+state.GetProperty("mode").GetString();
  if(layout==cameraLayout) return; // Do not replace an editor while its value is being typed.
  cameraLayout=layout; updatingCamera=true;
  try {
   CameraDevices.ItemsSource=state.GetProperty("cameras").EnumerateArray().Select(c=>new CameraChoice(c.GetProperty("id").GetString()!,c.GetProperty("label").GetString()!)).ToList();
   CameraDevices.SelectedItem=CameraDevices.Items.Cast<CameraChoice>().FirstOrDefault(c=>c.Id==id.GetString());
   CameraModes.ItemsSource=state.GetProperty("modes").EnumerateArray().Select(m=>new CameraChoice(m.GetProperty("id").GetString()!, $"{m.GetProperty("width").GetInt32()}×{m.GetProperty("height").GetInt32()} · {m.GetProperty("low").GetInt32()}–{m.GetProperty("high").GetInt32()} кадр/с")).ToList();
   CameraModes.SelectedItem=CameraModes.Items.Cast<CameraChoice>().FirstOrDefault(m=>m.Id==state.GetProperty("mode").GetString());
   CameraRotation.SelectedIndex=rotation/90; CameraMirror.IsChecked=mirror;
   CameraControls.Children.Clear(); CameraProControls.Children.Clear();
   foreach(var item in state.GetProperty("controls").EnumerateArray()) AddCameraControl(item);
  } finally { updatingCamera=false; }
 }
 private void AddCameraControl(JsonElement item) {
  string key=item.GetProperty("key").GetString()!;
  var row=new StackPanel { Margin=new Thickness(0,0,0,16) };
  row.Children.Add(new TextBlock { Text=item.GetProperty("label").GetString(),TextWrapping=TextWrapping.Wrap });
  bool has=item.TryGetProperty("value",out var value);
  if(item.TryGetProperty("boolean",out _)) {
   var check=new CheckBox { Content="Включено",IsChecked=has && value.GetBoolean(),IsThreeState=false };
   check.Click+=async(_,_)=> { if(check.IsChecked is bool b) await SendCamera(new {type="cameraSet",controls=new Dictionary<string,object>{{key,b}}}); };
   row.Children.Add(check);
  } else if(item.TryGetProperty("values",out var values)) {
   var combo=new ComboBox { DisplayMemberPath="Label" };
   combo.ItemsSource=values.EnumerateArray().Select(v=>new CameraChoice(v.GetInt32().ToString(CultureInfo.InvariantCulture),CameraModeLabel(key,v.GetInt32()))).ToList();
   combo.SelectedItem=has?combo.Items.Cast<CameraChoice>().FirstOrDefault(c=>c.Id==value.GetInt32().ToString(CultureInfo.InvariantCulture)):null;
   combo.SelectionChanged+=async(_,_)=> { if(!updatingCamera && combo.SelectedItem is CameraChoice c) await SendCamera(new {type="cameraSet",controls=new Dictionary<string,object>{{key,int.Parse(c.Id,CultureInfo.InvariantCulture)}}}); };
   row.Children.Add(combo);
  } else {
   double min=item.GetProperty("min").GetDouble(),max=item.GetProperty("max").GetDouble(),step=item.GetProperty("step").GetDouble();
   row.Children.Add(new TextBlock { Text=$"От {min:G6} до {max:G6} · шаг {step:G6}",Foreground=new SolidColorBrush(Color.FromRgb(154,154,168)) });
   var editor=new TextBox { Text=has?value.GetDouble().ToString("G9",CultureInfo.CurrentCulture):"",MinWidth=120,HorizontalAlignment=HorizontalAlignment.Left };
   var apply=new Button { Content="Применить",HorizontalAlignment=HorizontalAlignment.Left };
   apply.Click+=async(_,_)=> {
    if(!double.TryParse(editor.Text,NumberStyles.Float,CultureInfo.CurrentCulture,out double n) || !double.IsFinite(n) || n<min || n>max || (step>=1 && n!=Math.Round(n))) { CameraStatus.Text="Введите число из показанного диапазона."; return; }
    await SendCamera(new {type="cameraSet",controls=new Dictionary<string,object>{{key,n}}});
   };
   row.Children.Add(editor); row.Children.Add(apply);
  }
  if(key is "manualExposure" or "iso" or "exposureMs" or "videoStabilization" or "opticalStabilization") CameraProControls.Children.Add(row); else CameraControls.Children.Add(row);
 }
 private static string CameraModeLabel(string key,int value)=>key switch {
  "focusMode"=>value switch {0=>"Ручной / без автофокуса",1=>"Однократный автофокус",2=>"Макро",3=>"Непрерывный · видео",4=>"Непрерывный · фото",5=>"Расширенная глубина резкости",_=>$"Режим {value}"},
  "awbMode"=>value switch {0=>"Выключен",1=>"Автоматический",2=>"Лампа накаливания",3=>"Люминесцентный",4=>"Тёплый люминесцентный",5=>"Дневной свет",6=>"Облачно",7=>"Сумерки",8=>"Тень",_=>$"Режим {value}"},
  _=>value switch {0=>"Выключена",1=>"Включена",2=>"Стабилизация предпросмотра",_=>$"Режим {value}"}
 };
 internal void SmokeCameraPanel() {
  using var doc=JsonDocument.Parse("""
  {"running":true,"ready":true,"status":"Проверка камеры","actualFps":30,"cameraId":"0","mode":"640x480@30-30","rotation":90,"mirror":true,
   "cameras":[{"id":"0","label":"Задняя камера"}],"modes":[{"id":"640x480@30-30","width":640,"height":480,"low":30,"high":30}],
   "controls":[{"key":"zoom","label":"Масштаб","min":1,"max":4,"step":0.1,"value":1},{"key":"focusMode","label":"Фокус","values":[0,3],"value":3},{"key":"aeLock","label":"Блокировка экспозиции","boolean":true,"value":false},{"key":"iso","label":"Светочувствительность","min":100,"max":800,"step":1,"value":100}]}
  """);
  MainTabs.SelectedIndex=2;
  UpdateCamera(doc.RootElement);
  if(CameraDevices.Items.Count!=1 || CameraModes.Items.Count!=1 || CameraControls.Children.Count!=3 || CameraProControls.Children.Count!=1 || CameraPreview.LayoutTransform is not TransformGroup {Children.Count:2}) throw new InvalidOperationException("Не построен интерфейс камеры");
  var pixels=System.Buffers.ArrayPool<byte>.Shared.Rent(640*480*4);
  using(var frame=new VideoFrame(640,480,pixels,1,"Проверка")) PresentCameraFrame(frame);
  if(CameraPreview.Source is not WriteableBitmap {PixelWidth:640,PixelHeight:480}) throw new InvalidOperationException("Нет предпросмотра камеры");
  UpdateLayout();
  if(CameraPreview.ActualWidth<=0 || CameraPreview.ActualHeight<=0 || CameraSelectors.ActualWidth>ActualWidth) throw new InvalidOperationException("Камера не размещена в минимальном окне");
  CameraLost();
  if(CameraPreview.Source is not null || CameraSelectors.IsEnabled) throw new InvalidOperationException("Разрыв не очистил камеру");
  Log.Write("Проверка Camera UI: динамические элементы, Профи, поворот/отражение, кадр и отключение пройдены");
 }
 private async Task SendCamera(object message) {
  try { await connection.Send(message); CameraStatus.Text="Применяем настройку…"; }
  catch(Exception e) { Log.Write("Параметр камеры не доставлен",e); CameraStatus.Text="Настройка не доставлена. Подключите телефон снова."; }
 }
 private async void CameraDevice_Changed(object sender,SelectionChangedEventArgs e) { if(!updatingCamera && CameraDevices.SelectedItem is CameraChoice c) await SendCamera(new {type="cameraSet",cameraId=c.Id}); }
 private async void CameraMode_Changed(object sender,SelectionChangedEventArgs e) { if(!updatingCamera && CameraModes.SelectedItem is CameraChoice c) await SendCamera(new {type="cameraSet",mode=c.Id}); }
 private async void CameraRotation_Changed(object sender,SelectionChangedEventArgs e) { if(!updatingCamera && CameraRotation.SelectedItem is ComboBoxItem {Tag:string tag} && int.TryParse(tag,out int angle)) await SendCamera(new {type="cameraSet",rotation=angle}); }
 private async void CameraMirror_Click(object sender,RoutedEventArgs e) { if(!updatingCamera) await SendCamera(new {type="cameraSet",mirror=CameraMirror.IsChecked==true}); }
 private async void CameraReset_Click(object sender,RoutedEventArgs e) { cameraLayout=""; await SendCamera(new {type="cameraSet",reset=true}); }
}
