using System.Windows;
namespace Lenscast;
public partial class App : Application
{
 protected override void OnStartup(StartupEventArgs e) {
  DispatcherUnhandledException += (_, args) => { Log.Write("Необработанная ошибка", args.Exception); MessageBox.Show("Не удалось выполнить действие. Сохраните журнал в настройках и перезапустите Lenscast.", "Lenscast"); args.Handled = true; };
  base.OnStartup(e);
 }
}
