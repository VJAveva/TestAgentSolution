using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TestControllerGrpc.Identity;

namespace TestControllerGrpc.Controls;

/// <summary>
/// Badge displaying current user identity with role-specific styling.
/// Handles all variants: Administrator, SeniorManager, Engineer, Guest, Observer, Default user.
/// </summary>
public partial class UserIdentityBadge : UserControl
{
    public static readonly DependencyProperty RoleProperty =
        DependencyProperty.Register(nameof(Role), typeof(string), typeof(UserIdentityBadge),
            new PropertyMetadata("Default", OnRoleChanged));

    public static readonly DependencyProperty DisplayNameProperty =
        DependencyProperty.Register(nameof(DisplayName), typeof(string), typeof(UserIdentityBadge),
            new PropertyMetadata("Default user"));

    public string Role
    {
        get => (string)GetValue(RoleProperty);
        set => SetValue(RoleProperty, value);
    }

    public string DisplayName
    {
        get => (string)GetValue(DisplayNameProperty);
        set => SetValue(DisplayNameProperty, value);
    }

    public string RoleIcon => Role switch
    {
        "Administrator" => "🛡",
        "SeniorManager" => "👔",
        "Engineer" => "🔧",
        "Guest" => "🎫",
        "Observer" => "👁",
        _ => "👤", // Default user
    };

    public string RoleLabel => Role switch
    {
        "Administrator" => "Admin",
        "SeniorManager" => "Sr. Mgr",
        "Engineer" => "Engineer",
        "Guest" => "Guest",
        "Observer" => "Observer",
        _ => "Default",
    };

    public bool IsDefaultUser => Role is "Default" or "" or null;

    public Brush BadgeBorderBrush => Role switch
    {
        "Administrator" => new SolidColorBrush(Color.FromRgb(0xF3, 0x8B, 0xA8)), // Red
        "SeniorManager" => new SolidColorBrush(Color.FromRgb(0xFA, 0xB3, 0x87)), // Peach
        "Engineer" => new SolidColorBrush(Color.FromRgb(0x89, 0xB4, 0xFA)),       // Blue
        "Guest" => new SolidColorBrush(Color.FromRgb(0xA6, 0xE3, 0xA1)),          // Green
        "Observer" => new SolidColorBrush(Color.FromRgb(0x93, 0x99, 0xB2)),       // Gray
        _ => new SolidColorBrush(Color.FromRgb(0x93, 0x99, 0xB2)),               // Gray/dashed
    };

    public Brush BadgeBackground => Role switch
    {
        "Administrator" => new SolidColorBrush(Color.FromArgb(0x20, 0xF3, 0x8B, 0xA8)),
        "SeniorManager" => new SolidColorBrush(Color.FromArgb(0x20, 0xFA, 0xB3, 0x87)),
        "Engineer" => new SolidColorBrush(Color.FromArgb(0x20, 0x89, 0xB4, 0xFA)),
        "Guest" => new SolidColorBrush(Color.FromArgb(0x20, 0xA6, 0xE3, 0xA1)),
        "Observer" => new SolidColorBrush(Color.FromArgb(0x20, 0x93, 0x99, 0xB2)),
        _ => Brushes.Transparent,
    };

    public UserIdentityBadge()
    {
        InitializeComponent();
        DataContext = this;
    }

    private static void OnRoleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is UserIdentityBadge badge)
        {
            badge.DataContext = null;
            badge.DataContext = badge;
        }
    }
}
