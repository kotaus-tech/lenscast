using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace Lenscast;

public partial class MainWindow {
 private VideoFrame? pendingFrame;
 private int renderQueued;
 private WriteableBitmap? bitmap;
 private bool pointerDown,canControl;
 private double lastX,lastY;
 private readonly System.Windows.Threading.DispatcherTimer gestureHeartbeat=new() { Interval=TimeSpan.FromMilliseconds(250) };
 private Image? capturedImage,fullImage;
 private Window? fullWindow;
 private int imageWidth,imageHeight,presented;
 private long statsAt=Environment.TickCount64;
 private readonly object inputLock=new();
 private int inputEpoch;
 private readonly LinkedList<(string Kind,object Message,Func<object,Task> Send,int Epoch)> inputs=new();
 private void ResetInput() { CancelPointer(); lock(inputLock) { inputEpoch++; inputs.Clear(); } }
 private bool sendingInput;
 private void ReceiveFrame(VideoFrame frame) {
  if(closing) { frame.Dispose(); return; }
  Interlocked.Exchange(ref pendingFrame,frame)?.Dispose();
  if(Interlocked.Exchange(ref renderQueued,1)==0) Dispatcher.InvokeAsync(PresentFrame);
 }
 private void PresentFrame() {
  var frame=Interlocked.Exchange(ref pendingFrame,null); Interlocked.Exchange(ref renderQueued,0);
  if(frame is null) return;
  using(frame) {
   if(closing) return;
   imageWidth=frame.Width; imageHeight=frame.Height;
   if(bitmap is null || bitmap.PixelWidth!=frame.Width || bitmap.PixelHeight!=frame.Height) {
    bitmap=new WriteableBitmap(frame.Width,frame.Height,96,96,PixelFormats.Bgra32,null);
    Preview.Source=bitmap; if(fullImage!=null) fullImage.Source=bitmap;
   }
   bitmap.WritePixels(new Int32Rect(0,0,frame.Width,frame.Height),frame.Pixels,frame.Width*4,0);
   VideoHint.Visibility=Visibility.Collapsed;
   presented++;
   var now=Environment.TickCount64;
   if(now-statsAt>=1000) { VideoStats.Text=$"{frame.Width}×{frame.Height} · показано {presented*1000.0/(now-statsAt):F0} кадр/с · {frame.Decoder}"; statsAt=now; presented=0; }
  }
 }
 private (double X,double Y)? Position(Image image,MouseEventArgs e,bool clamp) {
  if(imageWidth==0 || imageHeight==0) return null;
  double scale=Math.Min(image.ActualWidth/imageWidth,image.ActualHeight/imageHeight);
  if(scale<=0) return null;
  var p=e.GetPosition(image);
  double x=(p.X-(image.ActualWidth-imageWidth*scale)/2)/(imageWidth*scale);
  double y=(p.Y-(image.ActualHeight-imageHeight*scale)/2)/(imageHeight*scale);
  if(!clamp && (x<0 || x>1 || y<0 || y>1)) return null;
  return(Math.Clamp(x,0,1),Math.Clamp(y,0,1));
 }
 private void Preview_Down(object sender,MouseButtonEventArgs e) {
  if(!canControl || !connection.IsReady) { Status.Text="Разрешите управление в специальных возможностях Lenscast на телефоне."; return; }
  if(sender is not Image image || Position(image,e,false) is not {} p) return;
  pointerDown=true; capturedImage=image; image.CaptureMouse(); Touch("down",p.X,p.Y); e.Handled=true;
 }
 private void Preview_Move(object sender,MouseEventArgs e) {
  if(pointerDown && sender is Image image && Position(image,e,true) is {} p) Touch("move",p.X,p.Y);
 }
 private void Preview_Up(object sender,MouseButtonEventArgs e) {
  if(!pointerDown || sender is not Image image) return;
  if(Position(image,e,true) is {} p) Touch("up",p.X,p.Y); else QueueInput("cancel",new {type="touch",action="cancel"});
  pointerDown=false; capturedImage=null; image.ReleaseMouseCapture(); e.Handled=true;
 }
 private void Preview_Lost(object sender,MouseEventArgs e)=>CancelPointer();
 private void CancelPointer() {
  if(!pointerDown) return;
  pointerDown=false; var image=capturedImage; capturedImage=null;
  QueueInput("cancel",new {type="touch",action="cancel"}); image?.ReleaseMouseCapture();
 }
 private void Touch(string action,double x,double y) { lastX=x; lastY=y; QueueInput(action,new {type="touch",action,x,y}); }
 private void QueueInput(string kind,object message) {
  Func<object,Task> bound;
  try { bound=connection.BindSender(); } catch { return; }
  lock(inputLock) {
   if(kind=="move" && inputs.Last?.Value.Kind=="move") inputs.Last.Value=(kind,message,bound,inputEpoch);
   else if(inputs.Count<16) inputs.AddLast((kind,message,bound,inputEpoch));
   else { inputs.Clear(); connection.Dispose(); Status.Text="Управление задержалось. Подключитесь к телефону снова."; return; }
   if(sendingInput) return; sendingInput=true;
  }
  _=Task.Run(async()=> {
   while(true) {
    (string Kind,object Message,Func<object,Task> Send,int Epoch) next;
    lock(inputLock) { if(inputs.First is null) { sendingInput=false; return; } next=inputs.First.Value; inputs.RemoveFirst(); }
    try { await next.Send(next.Message); }
    catch(Exception e) {
     Log.Write("Жест не доставлен",e);
     lock(inputLock) { if(next.Epoch!=inputEpoch) continue; inputs.Clear(); sendingInput=false; }
     await Dispatcher.InvokeAsync(()=> { if(next.Epoch!=inputEpoch) return; connection.Dispose(); pointerDown=false; capturedImage?.ReleaseMouseCapture(); capturedImage=null; Status.Text="Управление прервано. Подключитесь к телефону снова."; });
     return;
    }
   }
  });
 }
 private void Preview_Wheel(object sender,MouseWheelEventArgs e) {
  if(!canControl || pointerDown || sender is not Image image || Position(image,e,false) is not {} p) return;
  Touch("down",p.X,p.Y); double target=Math.Clamp(p.Y+Math.Sign(e.Delta)*0.18,0.01,0.99);
  Touch("move",p.X,target); Touch("up",p.X,target); e.Handled=true;
 }
 private void Navigation_Click(object sender,RoutedEventArgs e) {
  CancelPointer();
  if(!canControl || !connection.IsReady) { Status.Text="Разрешите управление в специальных возможностях Lenscast на телефоне."; return; }
  QueueInput("navigation",new {type="navigation",action=((Button)sender).Tag.ToString()});
 }
 private void Fullscreen_Click(object sender,RoutedEventArgs e) {
  if(fullWindow!=null) { fullWindow.Activate(); return; }
  fullImage=new Image { Source=bitmap,Stretch=Stretch.Uniform };
  fullImage.MouseLeftButtonDown+=Preview_Down; fullImage.MouseMove+=Preview_Move; fullImage.MouseLeftButtonUp+=Preview_Up; fullImage.LostMouseCapture+=Preview_Lost; fullImage.MouseWheel+=Preview_Wheel;
  fullWindow=new Window { Owner=this,Title="Lenscast · экран · Esc — выйти",WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,WindowState=WindowState.Maximized,Background=new SolidColorBrush(Color.FromRgb(14,14,17)),Topmost=Topmost,Content=fullImage };
  fullWindow.PreviewKeyDown+=(_,key)=>{ if(key.Key==Key.Escape) { fullWindow?.Close(); key.Handled=true; } };
  fullWindow.Deactivated+=(_,_)=>CancelPointer();
  fullWindow.Closed+=(_,_)=>{ CancelPointer(); fullWindow=null; fullImage=null; Activate(); };
  fullWindow.Show();
 }
 private void Topmost_Changed(object sender,RoutedEventArgs e) { Topmost=((CheckBox)sender).IsChecked==true; if(fullWindow!=null) fullWindow.Topmost=Topmost; }
}
