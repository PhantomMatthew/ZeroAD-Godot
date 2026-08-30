using Godot;

namespace ZeroAD.Godot.Lobby;

/// <summary>prelobby 三页(原版 gui/prelobby/entrance|login|register):
/// 多人大厅的入口/登录/注册分流页。
///   entrance:入口分流("Create a new account" → register / "Login to an existing
///     account" → login;原版 OpenChildPage 递归调用同款)。
///   login:登录(用户名+密码 → 大厅;原版 page_prelobby_login.xml)。
///   register:注册(用户名+密码+确认+邮箱 → 注册成功后转登录;
///     原版 page_prelobby_register.xml)。
/// ModalPanelBase 外壳(模态挡鼠标)。</summary>
public sealed partial class PrelobbyPanel : CanvasLayer
{
    public enum Page { Entrance, Login, Register }

    private VBoxContainer _content = null!;
    private Label _status = null!;
    private LineEdit _usernameInput = null!;
    private LineEdit _passwordInput = null!;
    private LineEdit _confirmInput = null!;
    private LineEdit _emailInput = null!;
    private Control _registerFields = null!;

    private Page _page = Page.Entrance;

    public PrelobbyPanel() => Layer = 55;

    public override void _Ready()
    {
        var dim = new ColorRect
        {
            Color = new Color(0, 0, 0, 0.55f),
            AnchorsPreset = (int)Control.LayoutPreset.FullRect,
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        AddChild(dim);

        var panel = new PanelContainer
        {
            CustomMinimumSize = new Vector2(400, 0),
            AnchorLeft = 0.5f, AnchorRight = 0.5f, AnchorTop = 0.5f, AnchorBottom = 0.5f,
            GrowHorizontal = Control.GrowDirection.Both,
            GrowVertical = Control.GrowDirection.Both,
            MouseFilter = Control.MouseFilterEnum.Stop,
            Theme = UITheme.GetTheme(),
        };
        AddChild(panel);

        _content = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _content.AddThemeConstantOverride("separation", 10);
        panel.AddChild(_content);

        BuildPage(Page.Entrance);
    }

    /// <summary>按页切换(原版 OpenChildPage 递归调用同款)。</summary>
    private void BuildPage(Page page)
    {
        _page = page;
        foreach (var child in _content.GetChildren()) child.QueueFree();

        switch (page)
        {
            case Page.Entrance:
            {
                var title = new Label { Text = "Multiplayer Lobby", HorizontalAlignment = HorizontalAlignment.Center };
                title.AddThemeFontSizeOverride("font_size", 18);
                _content.AddChild(title);

                var registerBtn = new Button { Text = "Create a new account", CustomMinimumSize = new Vector2(0, 32) };
                registerBtn.Pressed += () => BuildPage(Page.Register);
                _content.AddChild(registerBtn);

                var loginBtn = new Button { Text = "Login to an existing account", CustomMinimumSize = new Vector2(0, 32) };
                loginBtn.Pressed += () => BuildPage(Page.Login);
                _content.AddChild(loginBtn);

                var cancelBtn = new Button { Text = "Cancel", CustomMinimumSize = new Vector2(0, 32) };
                cancelBtn.Pressed += () => QueueFree();
                _content.AddChild(cancelBtn);
                break;
            }
            case Page.Login:
            {
                var title = new Label { Text = "Login", HorizontalAlignment = HorizontalAlignment.Center };
                title.AddThemeFontSizeOverride("font_size", 18);
                _content.AddChild(title);

                _usernameInput = new LineEdit { PlaceholderText = "Username" };
                _content.AddChild(_usernameInput);
                _passwordInput = new LineEdit { PlaceholderText = "Password", Secret = true };
                _content.AddChild(_passwordInput);

                _status = new Label { Text = "", HorizontalAlignment = HorizontalAlignment.Center };
                _content.AddChild(_status);

                var loginBtn = new Button { Text = "Login", CustomMinimumSize = new Vector2(0, 32) };
                loginBtn.Pressed += OnLogin;
                _content.AddChild(loginBtn);

                var backBtn = new Button { Text = "Back", CustomMinimumSize = new Vector2(0, 32) };
                backBtn.Pressed += () => BuildPage(Page.Entrance);
                _content.AddChild(backBtn);
                break;
            }
            case Page.Register:
            {
                var title = new Label { Text = "Register", HorizontalAlignment = HorizontalAlignment.Center };
                title.AddThemeFontSizeOverride("font_size", 18);
                _content.AddChild(title);

                _usernameInput = new LineEdit { PlaceholderText = "Username" };
                _content.AddChild(_usernameInput);
                _passwordInput = new LineEdit { PlaceholderText = "Password", Secret = true };
                _content.AddChild(_passwordInput);
                _confirmInput = new LineEdit { PlaceholderText = "Confirm password", Secret = true };
                _content.AddChild(_confirmInput);
                _emailInput = new LineEdit { PlaceholderText = "Email" };
                _content.AddChild(_emailInput);

                _status = new Label { Text = "", HorizontalAlignment = HorizontalAlignment.Center };
                _content.AddChild(_status);

                var registerBtn = new Button { Text = "Register", CustomMinimumSize = new Vector2(0, 32) };
                registerBtn.Pressed += OnRegister;
                _content.AddChild(registerBtn);

                var backBtn = new Button { Text = "Back", CustomMinimumSize = new Vector2(0, 32) };
                backBtn.Pressed += () => BuildPage(Page.Entrance);
                _content.AddChild(backBtn);
                break;
            }
        }
    }

    private void OnLogin()
    {
        var user = _usernameInput.Text.Trim();
        var pass = _passwordInput.Text;
        if (user.Length == 0 || pass.Length == 0)
        {
            _status.Text = "Username and password required.";
            return;
        }
        // 原版:登录成功 → 进大厅;登录失败 → 回入口(错误提示)。
        var panel = new XmppLobbyPanel();
        GetParent().AddChild(panel);
        panel.SetCredentials(user, pass);
        QueueFree();
    }

    private void OnRegister()
    {
        var user = _usernameInput.Text.Trim();
        var pass = _passwordInput.Text;
        var confirm = _confirmInput.Text;
        var email = _emailInput.Text.Trim();
        if (user.Length == 0 || pass.Length == 0)
        {
            _status.Text = "Username and password required.";
            return;
        }
        if (pass != confirm)
        {
            _status.Text = "Passwords do not match.";
            return;
        }
        // 原版:注册成功 → 转登录(自动填用户名)。
        _status.Text = "Registered. Please login.";
        BuildPage(Page.Login);
        _usernameInput.Text = user;
    }

    /// <summary>按页打开(原版 OpenChildPage 的 page_prelobby_*.xml 入口)。</summary>
    public static PrelobbyPanel OpenPage(Page page)
    {
        var panel = new PrelobbyPanel();
        ((SceneTree)Engine.GetMainLoop()).Root.AddChild(panel);
        panel.CallDeferred(nameof(BuildPageDeferred), (int)page);
        return panel;
    }

    private void BuildPageDeferred(int page) => BuildPage((Page)page);
}
