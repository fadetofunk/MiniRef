using System.Configuration;
using System.Data;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace MiniRef.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    /// <summary>Defaults spell check on for every TextBox/RichTextBox (most in this app are
    /// free-form prose -- shot narrative, descriptions, retention notes -- with only a handful
    /// of name/path/code fields opting back out locally). Set via metadata rather than an
    /// App.xaml implicit Style: an implicit Style targeting these types at the Application level
    /// -- even with BasedOn -- shadows the Fluent theme's own same-keyed default style, silently
    /// falling back to the plain (always-light) Aero2 style for every TextBox/RichTextBox in the
    /// app instead of following the Windows light/dark setting.</summary>
    static App()
    {
        SpellCheck.IsEnabledProperty.OverrideMetadata(typeof(TextBoxBase), new FrameworkPropertyMetadata(true));
    }
}

