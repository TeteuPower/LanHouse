using System.Drawing;
using VirtualLan.Core.Diagnostics;
using VirtualLan.Node;
using VirtualLan.Node.Peers;
using VirtualLan.Node.Tap;

namespace VirtualLan.App;

/// <summary>
/// Janela única do VirtualLan. Preenche relay/rede/senha, clica Conectar; na primeira vez
/// instala o adaptador virtual sozinho. Opcionalmente hospeda o relay neste PC (modo servidor).
///
/// A GUI só observa o <see cref="NodeService"/>: eventos e um snapshot de peers a cada segundo.
/// Toda a lógica de rede continua no motor; aqui não há regra de negócio.
/// </summary>
internal sealed class MainForm : Form
{
    private const string AutoAdapter = "(detectar automaticamente)";
    private const string JoinLabel = "Servidor do amigo (host:porta):";
    private const string HostLabel = "Porta deste servidor (padrão 7777):";

    private readonly AppSettings _settings = AppSettings.Load();

    // Controles
    private readonly CheckBox _hostCheck = new();
    private readonly Label _serverLabel = new();
    private readonly TextBox _relayBox = new();
    private readonly TextBox _networkBox = new();
    private readonly TextBox _passwordBox = new();
    private readonly CheckBox _showPassword = new();
    private readonly CheckBox _rememberPassword = new();
    private readonly ComboBox _adapterCombo = new();
    private readonly CheckBox _verboseCheck = new();
    private readonly Button _connectButton = new();
    private readonly Button _disconnectButton = new();
    private readonly TableLayoutPanel _sharePanel = new();
    private readonly TextBox _shareBox = new();
    private readonly GroupBox _peersGroup = new();
    private readonly ListView _peersList = new();
    private readonly RichTextBox _log = new();
    private readonly Label _statusDot = new();
    private readonly Label _statusLabel = new();
    private readonly NotifyIcon _tray = new();
    private readonly System.Windows.Forms.Timer _peersTimer = new() { Interval = 1000 };

    // Estado de runtime
    private NodeService? _node;
    private CancellationTokenSource? _nodeCts;
    private Task? _nodeTask;
    private RelayHost? _relayHost;
    private bool _connected;
    private bool _busy;

    public MainForm()
    {
        Text = "VirtualLan — rede LAN virtual";
        Font = new Font("Segoe UI", 9f);
        ClientSize = new Size(760, 620);
        MinimumSize = new Size(640, 560);
        StartPosition = FormStartPosition.CenterScreen;

        BuildUi();
        BuildTray();

        LoadSettingsIntoUi();
        PopulateAdapters();
        ApplyStatus(Color.Gray, "Desconectado");

        Log.MessageLogged += OnLogMessage;
        _peersTimer.Tick += OnPeersTick;
        FormClosing += OnFormClosing;
        Resize += OnResize;

        AcceptButton = _connectButton;
    }

    // ==================================================================== Construção da UI

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(10),
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        root.Controls.Add(BuildConnectionGroup(), 0, 0);
        root.Controls.Add(BuildContentSplit(), 0, 1);
        root.Controls.Add(BuildStatusBar(), 0, 2);

        Controls.Add(root);
    }

    private Control BuildConnectionGroup()
    {
        var group = new GroupBox
        {
            Text = "Conexão",
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(10),
        };

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        int row = 0;

        // Modo servidor
        _hostCheck.Text = "Hospedar o relay neste PC (eu sou o servidor)";
        _hostCheck.AutoSize = true;
        _hostCheck.Margin = new Padding(3, 3, 3, 8);
        _hostCheck.CheckedChanged += OnHostModeChanged;
        grid.Controls.Add(_hostCheck, 0, row);
        grid.SetColumnSpan(_hostCheck, 2);
        row++;

        // Servidor / porta
        _serverLabel.Text = JoinLabel;
        _serverLabel.AutoSize = true;
        _serverLabel.Anchor = AnchorStyles.Left;
        _serverLabel.Margin = new Padding(3, 6, 8, 6);
        _relayBox.Dock = DockStyle.Fill;
        grid.Controls.Add(_serverLabel, 0, row);
        grid.Controls.Add(_relayBox, 1, row);
        row++;

        // Rede
        grid.Controls.Add(MakeLabel("Nome da rede:"), 0, row);
        _networkBox.Dock = DockStyle.Fill;
        grid.Controls.Add(_networkBox, 1, row);
        row++;

        // Senha
        grid.Controls.Add(MakeLabel("Senha:"), 0, row);
        _passwordBox.Dock = DockStyle.Fill;
        _passwordBox.UseSystemPasswordChar = true;
        grid.Controls.Add(_passwordBox, 1, row);
        row++;

        // Opções da senha
        _showPassword.Text = "Mostrar senha";
        _showPassword.AutoSize = true;
        _showPassword.CheckedChanged += (_, _) => _passwordBox.UseSystemPasswordChar = !_showPassword.Checked;
        _rememberPassword.Text = "Lembrar senha neste PC";
        _rememberPassword.AutoSize = true;
        _rememberPassword.Margin = new Padding(20, 3, 3, 3);
        grid.Controls.Add(FlowOf(_showPassword, _rememberPassword), 1, row);
        row++;

        // Adaptador
        grid.Controls.Add(MakeLabel("Adaptador:"), 0, row);
        _adapterCombo.Dock = DockStyle.Fill;
        _adapterCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        grid.Controls.Add(_adapterCombo, 1, row);
        row++;

        // Verbose
        _verboseCheck.Text = "Log detalhado (para diagnóstico)";
        _verboseCheck.AutoSize = true;
        _verboseCheck.CheckedChanged += (_, _) => Log.MinimumLevel = _verboseCheck.Checked ? LogLevel.Debug : LogLevel.Info;
        grid.Controls.Add(FlowOf(_verboseCheck), 1, row);
        row++;

        // Botões
        _connectButton.Text = "Conectar";
        _connectButton.AutoSize = true;
        _connectButton.Padding = new Padding(12, 4, 12, 4);
        _connectButton.Font = new Font(Font, FontStyle.Bold);
        _connectButton.Click += OnConnectClick;
        _disconnectButton.Text = "Desconectar";
        _disconnectButton.AutoSize = true;
        _disconnectButton.Padding = new Padding(12, 4, 12, 4);
        _disconnectButton.Enabled = false;
        _disconnectButton.Click += OnDisconnectClick;
        grid.Controls.Add(FlowOf(_connectButton, _disconnectButton), 1, row);
        row++;

        // Painel de compartilhamento (só no modo servidor, depois de conectar)
        BuildSharePanel();
        grid.Controls.Add(_sharePanel, 0, row);
        grid.SetColumnSpan(_sharePanel, 2);

        group.Controls.Add(grid);
        return group;
    }

    private void BuildSharePanel()
    {
        _sharePanel.Dock = DockStyle.Fill;
        _sharePanel.AutoSize = true;
        _sharePanel.ColumnCount = 3;
        _sharePanel.Visible = false;
        _sharePanel.Margin = new Padding(3, 8, 3, 3);
        _sharePanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _sharePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _sharePanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var label = new Label
        {
            Text = "Envie ao amigo:",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(3, 7, 8, 3),
            Font = new Font(Font, FontStyle.Bold),
        };

        _shareBox.Dock = DockStyle.Fill;
        _shareBox.ReadOnly = true;
        _shareBox.Font = new Font("Consolas", 10f);

        var copy = new Button { Text = "Copiar", AutoSize = true, Margin = new Padding(8, 3, 3, 3) };
        copy.Click += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(_shareBox.Text))
            {
                Clipboard.SetText(_shareBox.Text);
                ApplyStatus(Color.SeaGreen, "Endereço copiado — cole para o seu amigo.");
            }
        };

        _sharePanel.Controls.Add(label, 0, 0);
        _sharePanel.Controls.Add(_shareBox, 1, 0);
        _sharePanel.Controls.Add(copy, 2, 0);
    }

    private Control BuildContentSplit()
    {
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterWidth = 6,
        };

        // Participantes
        _peersGroup.Text = "Participantes";
        _peersGroup.Dock = DockStyle.Fill;
        _peersGroup.Padding = new Padding(8);

        _peersList.Dock = DockStyle.Fill;
        _peersList.View = View.Details;
        _peersList.FullRowSelect = true;
        _peersList.GridLines = true;
        _peersList.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        _peersList.Columns.Add("IP virtual", 90);
        _peersList.Columns.Add("MAC", 150);
        _peersList.Columns.Add("Estado", 90);
        _peersList.Columns.Add("Caminho", 260);
        _peersGroup.Controls.Add(_peersList);

        // Registro
        var logGroup = new GroupBox { Text = "Registro", Dock = DockStyle.Fill, Padding = new Padding(8) };
        _log.Dock = DockStyle.Fill;
        _log.ReadOnly = true;
        _log.BackColor = Color.FromArgb(24, 24, 24);
        _log.ForeColor = Color.Gainsboro;
        _log.Font = new Font("Consolas", 9f);
        _log.BorderStyle = BorderStyle.None;
        _log.WordWrap = false;
        logGroup.Controls.Add(_log);

        split.Panel1.Controls.Add(_peersGroup);
        split.Panel2.Controls.Add(logGroup);
        split.Panel1MinSize = 120;
        split.Panel2MinSize = 120;

        // SplitterDistance precisa ser aplicado depois que o container tem tamanho.
        split.HandleCreated += (_, _) =>
        {
            if (split.Height > 260) split.SplitterDistance = 160;
        };

        return split;
    }

    private Control BuildStatusBar()
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0, 6, 0, 0),
        };

        _statusDot.Text = "●";
        _statusDot.AutoSize = true;
        _statusDot.Font = new Font(Font.FontFamily, 12f);
        _statusDot.Margin = new Padding(3, 2, 4, 0);

        _statusLabel.AutoSize = true;
        _statusLabel.Margin = new Padding(0, 5, 0, 0);

        panel.Controls.Add(_statusDot);
        panel.Controls.Add(_statusLabel);
        return panel;
    }

    private void BuildTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Abrir", null, (_, _) => RestoreFromTray());
        menu.Items.Add("Sair", null, (_, _) => Close());

        _tray.Icon = SystemIcons.Application;
        _tray.Text = "VirtualLan";
        _tray.Visible = false;
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => RestoreFromTray();
    }

    private Label MakeLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(3, 6, 8, 6),
    };

    private static FlowLayoutPanel FlowOf(params Control[] controls)
    {
        var flow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0) };
        flow.Controls.AddRange(controls);
        return flow;
    }

    // ==================================================================== Preferências

    private void LoadSettingsIntoUi()
    {
        _hostCheck.Checked = _settings.HostMode;
        _networkBox.Text = _settings.Network;
        _rememberPassword.Checked = _settings.RememberPassword;
        _verboseCheck.Checked = _settings.Verbose;

        string? savedPassword = _settings.GetPassword();
        if (savedPassword is not null) _passwordBox.Text = savedPassword;

        // Aplica o rótulo/valor correto do campo servidor conforme o modo.
        ApplyHostModeUi();

        Log.MinimumLevel = _settings.Verbose ? LogLevel.Debug : LogLevel.Info;
    }

    private void SaveSettingsFromUi()
    {
        _settings.HostMode = _hostCheck.Checked;
        _settings.Network = _networkBox.Text.Trim();
        _settings.Verbose = _verboseCheck.Checked;
        _settings.RememberPassword = _rememberPassword.Checked;

        if (_hostCheck.Checked)
        {
            if (int.TryParse(_relayBox.Text.Trim(), out int p)) _settings.HostPort = p;
        }
        else
        {
            _settings.Relay = _relayBox.Text.Trim();
        }

        _settings.Adapter = _adapterCombo.SelectedItem as string == AutoAdapter ? "" : (_adapterCombo.SelectedItem as string ?? "");

        _settings.SetPassword(_rememberPassword.Checked ? _passwordBox.Text : null);
        _settings.Save();
    }

    private void PopulateAdapters()
    {
        _adapterCombo.Items.Clear();
        _adapterCombo.Items.Add(AutoAdapter);

        foreach (var adapter in TapAdapterLocator.FindAll())
            _adapterCombo.Items.Add(adapter.Name);

        // Seleciona o salvo, senão "VirtualLan", senão automático.
        object? target = null;
        if (!string.IsNullOrEmpty(_settings.Adapter) && _adapterCombo.Items.Contains(_settings.Adapter))
            target = _settings.Adapter;
        else if (_adapterCombo.Items.Contains(TapInstaller.DefaultAdapterName))
            target = TapInstaller.DefaultAdapterName;

        _adapterCombo.SelectedItem = target ?? AutoAdapter;
    }

    private string? SelectedAdapterOrNull()
        => _adapterCombo.SelectedItem as string is { } name && name != AutoAdapter ? name : null;

    // ==================================================================== Modo servidor

    private void OnHostModeChanged(object? sender, EventArgs e) => ApplyHostModeUi();

    private void ApplyHostModeUi()
    {
        if (_hostCheck.Checked)
        {
            _serverLabel.Text = HostLabel;
            _relayBox.Text = _settings.HostPort > 0 ? _settings.HostPort.ToString() : "7777";
        }
        else
        {
            _serverLabel.Text = JoinLabel;
            _relayBox.Text = _settings.Relay;
        }
    }

    // ==================================================================== Conectar

    private async void OnConnectClick(object? sender, EventArgs e)
    {
        if (_connected || _busy) return;

        string network = _networkBox.Text.Trim();
        string password = _passwordBox.Text;
        bool host = _hostCheck.Checked;

        if (network.Length == 0) { Warn("Informe o nome da rede."); return; }
        if (password.Length == 0) { Warn("Informe a senha da rede."); return; }

        string relayHost;
        int relayPort;

        if (host)
        {
            if (!int.TryParse(_relayBox.Text.Trim(), out relayPort) || relayPort is < 1 or > 65535)
            {
                Warn("Porta inválida. Use um número entre 1 e 65535 (padrão 7777).");
                return;
            }
            relayHost = "127.0.0.1";
        }
        else if (!TrySplitHostPort(_relayBox.Text.Trim(), 7777, out relayHost, out relayPort))
        {
            Warn("Endereço do servidor inválido. Use host:porta (ex.: 200.100.50.10:7777).");
            return;
        }

        SaveSettingsFromUi();
        SetBusy(true);
        Log.MinimumLevel = _verboseCheck.Checked ? LogLevel.Debug : LogLevel.Info;

        var progress = new Progress<string>(m => ApplyStatus(Color.DarkOrange, m));

        try
        {
            string? adapter = SelectedAdapterOrNull();

            if (TapAdapterLocator.FindAll().Count == 0)
            {
                ApplyStatus(Color.DarkOrange, "Primeira execução: preparando o adaptador de rede virtual...");
                await Task.Run(() => TapInstaller.EnsureInstalledAsync(
                    adapter ?? TapInstaller.DefaultAdapterName, progress, CancellationToken.None));
                PopulateAdapters();
                adapter = SelectedAdapterOrNull();
            }

            if (host)
            {
                _relayHost = new RelayHost(relayPort);
                await _relayHost.StartAsync(progress, CancellationToken.None);
                ShowShare(_relayHost.ShareAddress ?? "(não descobri seu IP público — veja o Registro)");
            }

            var options = new NodeOptions
            {
                RelayHost = relayHost,
                RelayPort = relayPort,
                NetworkName = network,
                Password = password,
                AdapterName = adapter,
                LocalPort = 0,
            };

            StartNode(options);

            _connected = true;
            SetBusy(false);
            UpdateButtons();
        }
        catch (Exception ex)
        {
            Log.Error("Não foi possível conectar", ex);
            CleanupToDisconnected(ex.Message);
            ShowError(ex.Message);
        }
    }

    private void StartNode(NodeOptions options)
    {
        _nodeCts = new CancellationTokenSource();
        _node = new NodeService(options);
        _node.StateChanged += OnNodeStateChanged;

        var node = _node;
        var token = _nodeCts.Token;
        _nodeTask = Task.Run(() => RunNodeAsync(node, token), CancellationToken.None);

        _peersTimer.Start();
    }

    private async Task RunNodeAsync(NodeService node, CancellationToken ct)
    {
        Exception? fault = null;
        try
        {
            await node.RunAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* desconexão pedida pelo usuário */ }
        catch (Exception ex)
        {
            fault = ex;
            Log.Error("A conexão terminou com erro", ex);
        }
        finally
        {
            UiInvoke(() => CleanupToDisconnected(fault?.Message));
        }
    }

    private void OnNodeStateChanged(NodeConnectionState state, string? detail) =>
        UiInvoke(() => ApplyNodeState(state, detail));

    private void ApplyNodeState(NodeConnectionState state, string? detail)
    {
        switch (state)
        {
            case NodeConnectionState.Resolving:
                ApplyStatus(Color.DarkOrange, "Abrindo o adaptador e conectando...");
                break;
            case NodeConnectionState.Connecting:
                ApplyStatus(Color.DarkOrange, "Registrando no servidor...");
                break;
            case NodeConnectionState.Connected:
                ApplyStatus(Color.SeaGreen, $"Conectado — seu IP virtual é {detail}. Abra o jogo em modo LAN.");
                break;
            case NodeConnectionState.Faulted:
                ApplyStatus(Color.Firebrick, detail ?? "Falha.");
                break;
        }
    }

    // ==================================================================== Desconectar / limpeza

    private void OnDisconnectClick(object? sender, EventArgs e)
    {
        if (!_connected) return;

        _disconnectButton.Enabled = false;
        ApplyStatus(Color.DarkOrange, "Desconectando...");
        _nodeCts?.Cancel(); // RunNodeAsync chama CleanupToDisconnected ao terminar
    }

    /// <summary>Volta a UI ao estado desconectado. Idempotente e à prova de nulos.</summary>
    private void CleanupToDisconnected(string? faultMessage)
    {
        _peersTimer.Stop();
        _peersList.Items.Clear();
        _peersGroup.Text = "Participantes";

        if (_node is not null)
        {
            _node.StateChanged -= OnNodeStateChanged;
            try { _node.Dispose(); } catch { /* já disposto */ }
            _node = null;
        }

        if (_nodeCts is not null) { try { _nodeCts.Dispose(); } catch { /* idem */ } _nodeCts = null; }
        _nodeTask = null;

        if (_relayHost is not null) { _relayHost.Dispose(); _relayHost = null; }

        HideShare();

        _connected = false;
        SetBusy(false);
        UpdateButtons();

        if (faultMessage is not null)
            ApplyStatus(Color.Firebrick, "Erro: " + faultMessage);
        else
            ApplyStatus(Color.Gray, "Desconectado");
    }

    // ==================================================================== Peers (timer)

    private void OnPeersTick(object? sender, EventArgs e)
    {
        if (_node is null) return;

        var peers = _node.SnapshotPeers();

        _peersList.BeginUpdate();
        _peersList.Items.Clear();

        foreach (var peer in peers)
        {
            var item = new ListViewItem(peer.VirtualIp);
            item.SubItems.Add(peer.Mac);
            item.SubItems.Add(peer.Path switch
            {
                PathState.Direct => "Direto",
                PathState.Punching => "Conectando",
                _ => "Via relay",
            });
            item.SubItems.Add(peer.PathDetail);
            _peersList.Items.Add(item);
        }

        _peersList.EndUpdate();
        _peersGroup.Text = peers.Count == 0 ? "Participantes (aguardando o amigo)" : $"Participantes ({peers.Count})";
    }

    // ==================================================================== Bandeja / janela

    private void OnResize(object? sender, EventArgs e)
    {
        if (WindowState == FormWindowState.Minimized)
        {
            Hide();
            _tray.Visible = true;
        }
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        _tray.Visible = false;
        Activate();
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        // Fechar pela bandeja/menu "Sair" ou pelo botão X encerra de verdade e desconecta limpo.
        if (_connected)
        {
            _nodeCts?.Cancel();
            try { _nodeTask?.Wait(TimeSpan.FromSeconds(2)); } catch { /* melhor-esforço */ }
            if (_relayHost is not null) { _relayHost.Dispose(); _relayHost = null; }
        }

        Log.MessageLogged -= OnLogMessage;
        _tray.Visible = false;
        _tray.Dispose();
    }

    // ==================================================================== Log / status helpers

    private void OnLogMessage(LogLevel level, string message) => UiInvoke(() => AppendLog(level, message));

    private void AppendLog(LogLevel level, string message)
    {
        Color color = level switch
        {
            LogLevel.Trace => Color.Gray,
            LogLevel.Debug => Color.DarkGray,
            LogLevel.Info => Color.Gainsboro,
            LogLevel.Warn => Color.Gold,
            _ => Color.Tomato,
        };

        // Limita o histórico para não crescer sem limite numa sessão longa.
        if (_log.TextLength > 200_000)
        {
            _log.SelectAll();
            _log.SelectedText = string.Empty;
        }

        _log.SelectionStart = _log.TextLength;
        _log.SelectionLength = 0;
        _log.SelectionColor = color;
        _log.AppendText($"{DateTime.Now:HH:mm:ss}  {message}{Environment.NewLine}");
        _log.SelectionColor = _log.ForeColor;
        _log.ScrollToCaret();
    }

    private void ApplyStatus(Color color, string text)
    {
        _statusDot.ForeColor = color;
        _statusLabel.Text = text;
    }

    private void ShowShare(string address)
    {
        _shareBox.Text = address;
        _sharePanel.Visible = true;
    }

    private void HideShare()
    {
        _sharePanel.Visible = false;
        _shareBox.Text = "";
    }

    private void UpdateButtons()
    {
        _connectButton.Enabled = !_connected && !_busy;
        _disconnectButton.Enabled = _connected && !_busy;
        SetInputsEnabled(!_connected && !_busy);
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        UpdateButtons();
        if (busy)
        {
            _connectButton.Enabled = false;
            _disconnectButton.Enabled = false;
        }
    }

    private void SetInputsEnabled(bool enabled)
    {
        _hostCheck.Enabled = enabled;
        _relayBox.Enabled = enabled;
        _networkBox.Enabled = enabled;
        _passwordBox.Enabled = enabled;
        _showPassword.Enabled = enabled;
        _rememberPassword.Enabled = enabled;
        _adapterCombo.Enabled = enabled;
        _verboseCheck.Enabled = enabled;
    }

    private void Warn(string message) =>
        MessageBox.Show(this, message, "VirtualLan", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    private void ShowError(string message) =>
        MessageBox.Show(this, message, "VirtualLan — erro", MessageBoxButtons.OK, MessageBoxIcon.Error);

    private void UiInvoke(Action action)
    {
        if (IsDisposed || Disposing) return;

        try
        {
            if (InvokeRequired) BeginInvoke(action);
            else action();
        }
        catch (ObjectDisposedException) { /* form fechando */ }
        catch (InvalidOperationException) { /* handle não criado ainda / fechando */ }
    }

    private static bool TrySplitHostPort(string text, int defaultPort, out string host, out int port)
    {
        host = text;
        port = defaultPort;

        if (text.Length == 0) return false;

        int colon = text.LastIndexOf(':');
        if (colon < 0) return true;

        host = text[..colon];
        return host.Length > 0 && int.TryParse(text[(colon + 1)..], out port) && port is > 0 and <= 65535;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { _node?.Dispose(); } catch { /* idem */ }
            try { _relayHost?.Dispose(); } catch { /* idem */ }
            _peersTimer.Dispose();
        }

        base.Dispose(disposing);
    }
}
