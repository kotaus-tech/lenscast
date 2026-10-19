using System.Windows;
namespace Lenscast;
public partial class App : Application
{
 public App() {
  AppDomain.CurrentDomain.UnhandledException += (_, args) => { if(args.ExceptionObject is Exception e) Log.Write("Ошибка инициализации приложения", e); };
 }
 internal static bool IsSmokeTest => Environment.GetCommandLineArgs().Contains("--ci-smoke");
 protected override void OnStartup(StartupEventArgs e) {
  DispatcherUnhandledException += (_, args) => { Log.Write("Необработанная ошибка", args.Exception); if(IsSmokeTest) { args.Handled=true; Shutdown(1); return; } MessageBox.Show("Не удалось выполнить действие. Сохраните журнал в настройках и перезапустите Lenscast.", "Lenscast"); args.Handled = true; };
  base.OnStartup(e);
  Wpf.Ui.Appearance.ApplicationAccentColorManager.Apply(System.Windows.Media.Color.FromRgb(143,124,240), Wpf.Ui.Appearance.ApplicationTheme.Dark);
  if (IsSmokeTest) {
   var timer=new System.Windows.Threading.DispatcherTimer { Interval=TimeSpan.FromSeconds(3) };
   timer.Tick += (_,_)=>{
    timer.Stop();
    var window=Windows.OfType<MainWindow>().FirstOrDefault();
    if(window is null || !window.IsLoaded || !window.IsVisible) { Shutdown(1); return; }
    window.SmokeCameraPanel();
    window.Width=760; window.Height=580; window.UpdateLayout();
    Log.Write("Проверка загрузки WPF и минимального размера пройдена");
    Shutdown(0);
   };
   timer.Start();
  }
 }
}
