// WPF + WinForms in the same project makes several type names ambiguous.
// Pin them once, globally, instead of scattering aliases across files.
global using Path = System.IO.Path;
global using File = System.IO.File;
global using Directory = System.IO.Directory;
global using Application = System.Windows.Application;
global using Brush = System.Windows.Media.Brush;
global using Brushes = System.Windows.Media.Brushes;
global using MessageBox = System.Windows.MessageBox;
global using MessageBoxButton = System.Windows.MessageBoxButton;
global using MessageBoxImage = System.Windows.MessageBoxImage;
global using MessageBoxResult = System.Windows.MessageBoxResult;
