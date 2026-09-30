using System;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using QRCoder;
using SyncNote.Core;
using SyncNote.Core.Interfaces;
using SyncNote.Core.Models;
using SyncNote.Core.Services;
using SyncNote.Windows.Services;

namespace SyncNote.Windows.ViewModels;

public sealed class PairingViewModel : INotifyPropertyChanged
{
    private readonly string _dbPath;
    private readonly string _deviceId;
    private readonly CancellationTokenSource _cts = new();
    private PairingService? _session;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnProp([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new(n));

    // UI state
    public bool IsIdle => _session == null;
    // QR виден всё время жизни сессии (включая ожидание пира).
    public bool IsGeneratingQr => _session != null && !IsDone && !HasError;
    public bool IsWaitingForPeer => _session != null && _tokenUsed == null && !IsDone && !HasError;
    public bool IsDone => _result != null;
    public bool HasError => _error != null;
    public bool IsIdleOrError => IsIdle || HasError;

    private string _hostIp = "—";
    public string HostIp { get => _hostIp; private set { _hostIp = value; OnProp(); } }

    private int _port;
    public int Port { get => _port; private set { _port = value; OnProp(); } }

    private BitmapSource? _qrBitmap;
    public BitmapSource? QrBitmap { get => _qrBitmap; private set { _qrBitmap = value; OnProp(); } }

    private string _statusText = "Нажмите «Сгенерировать QR-код» для начала сопряжения.";
    public string StatusText { get => _statusText; private set { _statusText = value; OnProp(); } }

    private string _resultSummary = "";
    public string ResultSummary { get => _resultSummary; private set { _resultSummary = value; OnProp(); } }

    private string? _error;
    public string? ErrorMessage { get => _error; private set { _error = value; OnProp(); } }

    private string? _tokenUsed;
    private SyncResult? _result;

    public ICommand StartPairingCommand { get; }
    public ICommand RetryCommand { get; }
    public ICommand CloseCommand { get; }

    public PairingViewModel(string dbPath, string deviceId)
    {
        _dbPath = dbPath;
        _deviceId = deviceId;
        StartPairingCommand = new RelayCommand(_ => StartPairing());
        RetryCommand = new RelayCommand(_ => { _error = null; StartPairing(); });
        CloseCommand = new RelayCommand(_ => RequestClose?.Invoke(this, EventArgs.Empty));
    }

    public event EventHandler? RequestClose;

    private void StartPairing()
    {
        _error = null;
        OnProp(nameof(HasError));
        _result = null;
        _tokenUsed = null;
        OnProp(nameof(IsDone));
        OnProp(nameof(IsIdle));
        OnProp(nameof(IsGeneratingQr));
        OnProp(nameof(IsWaitingForPeer));
        OnProp(nameof(HasError));

        // Определяем локальный IP (не loopback)
        string hostIp = "127.0.0.1";
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(ua.Address))
                    {
                        hostIp = ua.Address.ToString();
                        goto FoundIp;
                    }
                }
            }
        FoundIp: ;
        }
        catch { }

        // Стартуем сессию на фиксированном порту синка.
        _session = PairingService.Start(
            _dbPath, _deviceId, hostIp, PairingService.DefaultPort,
            Path.Combine(Path.GetDirectoryName(_dbPath) ?? ".", "tmp"));
        HostIp = _session.Host;
        Port = _session.Port;

        // QR bitmap
        byte[] png = _session.QrPng;
        using var ms = new MemoryStream(png);
        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.StreamSource = ms;
        img.EndInit();
        img.Freeze();
        QrBitmap = img;

        StatusText = "Ожидание подключения...";
        // Сессия создана после начального сброса флагов — уведомляем UI повторно,
        // иначе кнопки/панели останутся в idle-состоянии.
        OnProp(nameof(IsIdle));
        OnProp(nameof(IsGeneratingQr));
        OnProp(nameof(IsWaitingForPeer));
        OnProp(nameof(HasError));
        OnProp(nameof(IsDone));
        _ = Task.Run(() => WaitForPeerAsync());
    }

    private async void WaitForPeerAsync()
    {
        try
        {
            var res = _session!.WaitForPeerAsync(
                s => { StatusText = s; OnProp(nameof(StatusText)); },
                _cts.Token);
            // Токен сгорает в finally сессии — читаем до await.
            var tokenPresented = _session.Token;
            var resVal = await res;
            _result = resVal;
            _tokenUsed = tokenPresented;
            ResultSummary = $"Отправлено: {resVal.Pushed}, получено: {resVal.Pulled}, конфликтов: {resVal.Conflicts}";
            OnProp(nameof(IsDone));
            OnProp(nameof(IsWaitingForPeer));
            OnProp(nameof(ResultSummary));
        }
        catch (HelloRejectedException)
        {
            _error = "Чужой токен — в соединении отказано.";
            OnProp(nameof(HasError));
            OnProp(nameof(ErrorMessage));
            OnProp(nameof(IsWaitingForPeer));
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            OnProp(nameof(HasError));
            OnProp(nameof(ErrorMessage));
            OnProp(nameof(IsWaitingForPeer));
        }
        finally
        {
            _session.Dispose();
            _session = null;
            OnProp(nameof(IsGeneratingQr));
            OnProp(nameof(IsWaitingForPeer));
            OnProp(nameof(IsIdle));
            OnProp(nameof(IsIdleOrError));
        }
    }

    public void Dispose() => _cts.Cancel();
}

public sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? p) => canExecute?.Invoke(p) ?? true;
    public void Execute(object? p) => execute(p);
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}