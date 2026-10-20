using SoundSync.Models;
using SoundSync.Services;
using NAudio.CoreAudioApi;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Threading;

namespace SoundSync.ViewModels
{
    public class MainViewModel : INotifyPropertyChanged
    {
        private readonly IAudioEngine _audioEngine;
        private readonly INetworkStreamer _networkStreamer;
        private readonly ISettingsManager _settingsManager;
        private readonly Dispatcher _dispatcher;

        private DispatcherTimer? _defaultDevicePeakTimer;

        public ObservableCollection<DeviceItem> Devices { get; } = new ObservableCollection<DeviceItem>();

        private bool _isConnected;
        public bool IsConnected
        {
            get => _isConnected;
            set { _isConnected = value; OnPropertyChanged(); }
        }

        private string _statusText = "状态：未连接";
        public string StatusText
        {
            get => _statusText;
            set { _statusText = value; OnPropertyChanged(); }
        }

        private string _statusBrushKey = "StatusErrorBrush";
        public string StatusBrushKey
        {
            get => _statusBrushKey;
            set { _statusBrushKey = value; OnPropertyChanged(); }
        }

        private string _connectButtonTag = "Disconnected";
        public string ConnectButtonTag
        {
            get => _connectButtonTag;
            set { _connectButtonTag = value; OnPropertyChanged(); }
        }

        private string _connectButtonText = "连接并开始音频转发";
        public string ConnectButtonText
        {
            get => _connectButtonText;
            set { _connectButtonText = value; OnPropertyChanged(); }
        }

        private string _updateText = "SoundSync 有新版本可用！";
        public string UpdateText
        {
            get => _updateText;
            set { _updateText = value; OnPropertyChanged(); }
        }

        private bool _isUpdateBannerVisible;
        public bool IsUpdateBannerVisible
        {
            get => _isUpdateBannerVisible;
            set { _isUpdateBannerVisible = value; OnPropertyChanged(); }
        }

        private string _latestReleaseUrl = "https://github.com/sugumar247/SoundSync/releases";
        public string LatestReleaseUrl { get => _latestReleaseUrl; set { _latestReleaseUrl = value; OnPropertyChanged(); } }

        public RelayCommand ConnectCommand { get; }
        public RelayCommand RefreshCommand { get; }
        public RelayCommand DownloadUpdateCommand { get; }

        public MainViewModel(IAudioEngine audioEngine, INetworkStreamer networkStreamer, ISettingsManager settingsManager, Dispatcher dispatcher)
        {
            _audioEngine = audioEngine;
            _networkStreamer = networkStreamer;
            _settingsManager = settingsManager;
            _dispatcher = dispatcher;

            ConnectCommand = new RelayCommand(_ => ToggleConnect());
            RefreshCommand = new RelayCommand(_ => RefreshDevices(), _ => !IsConnected);
            DownloadUpdateCommand = new RelayCommand(_ => DownloadUpdate());

            LoadDevices();
            LoadSavedProfile();
            CheckForUpdatesAsync();
        }

        public void LoadDevices()
        {
            Devices.Clear();
            try
            {
                var activeDevices = _audioEngine.GetActiveRenderDevices();
                foreach (var d in activeDevices)
                {
                    var item = new DeviceItem
                    {
                        Device = d,
                        IsSelected = false,
                        SoftwareVolume = 1.0f
                    };
                    item.DelayChangedCallback = () => _audioEngine.UpdateRelativeDelays(Devices.ToList());
                    Devices.Add(item);
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("加载设备失败：" + ex.Message);
            }
        }

        public void RefreshDevices()
        {
            LoadDevices();
            LoadSavedProfile();
        }

        public void LoadSavedProfile()
        {
            var savedData = _settingsManager.LoadProfile();
            foreach (var saved in savedData)
            {
                var matchingItem = Devices.FirstOrDefault(i => i.Device.ID == saved.DeviceId);
                if (matchingItem != null)
                {
                    matchingItem.IsSelected = saved.IsSelected;
                    matchingItem.SoftwareVolume = saved.SoftwareVolume;
                    matchingItem.Delay = saved.Delay;
                    matchingItem.Bass = saved.Bass;
                    matchingItem.Mid = saved.Mid;
                    matchingItem.Treble = saved.Treble;
                }
            }
        }

        public void SaveProfile()
        {
            var data = Devices.Select(i => new SavedDeviceSettings
            {
                DeviceId = i.Device.ID,
                IsSelected = i.IsSelected,
                SoftwareVolume = i.SoftwareVolume,
                Delay = i.Delay,
                Bass = i.Bass,
                Mid = i.Mid,
                Treble = i.Treble
            }).ToList();
            _settingsManager.SaveProfile(data);
        }

        public void ToggleConnect()
        {
            if (IsConnected)
                Disconnect();
            else
                Connect();
        }

        private void Connect()
        {
            var selectedDevices = Devices.Where(i => i.IsSelected).ToList();
            if (selectedDevices.Count == 0)
            {
                System.Windows.MessageBox.Show("请至少勾选一个非默认输出设备。");
                return;
            }
            try
            {
                _audioEngine.Connect(selectedDevices, _networkStreamer, log => { }, _ =>
                    _dispatcher.BeginInvoke(new Action(Disconnect)));
                StartDefaultDevicePeakTimer();
                IsConnected = true;
                ConnectButtonText = "断开连接";
                ConnectButtonTag = "Connected";
                StatusText = $"收听：http://{GetLocalIPAddress()}:8090/?t={Services.LinkAuth.Token} | 正在转发音频";
                StatusBrushKey = "StatusSuccessBrush";
                SaveProfile();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(ex.Message);
                Disconnect();
            }
        }

        public void Disconnect()
        {
            _audioEngine.Disconnect(Devices.ToList());
            _networkStreamer.Stop();
            StopDefaultDevicePeakTimer();

            IsConnected = false;
            ConnectButtonText = "连接并开始音频转发";
            ConnectButtonTag = "Disconnected";
            StatusText = "状态：未连接";
            StatusBrushKey = "StatusErrorBrush";
        }

        private void StartDefaultDevicePeakTimer()
        {
            _defaultDevicePeakTimer = new DispatcherTimer();
            _defaultDevicePeakTimer.Interval = TimeSpan.FromMilliseconds(50);
            _defaultDevicePeakTimer.Tick += (s, e) =>
            {
                if (!IsConnected) return;
                try
                {
                    var enumerator = new MMDeviceEnumerator();
                    var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    var defaultItem = Devices.FirstOrDefault(i => i.Device.ID == defaultDevice.ID);
                    if (defaultItem != null)
                    {
                        defaultItem.PeakLevel = defaultDevice.AudioMeterInformation.MasterPeakValue;
                    }
                }
                catch { }
            };
            _defaultDevicePeakTimer.Start();
        }

        private void StopDefaultDevicePeakTimer()
        {
            if (_defaultDevicePeakTimer != null)
            {
                _defaultDevicePeakTimer.Stop();
                _defaultDevicePeakTimer = null;
            }
        }

        private async void CheckForUpdatesAsync()
        {
            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.UserAgent.ParseAdd("SoundSync-Client");
                string responseJson = await client.GetStringAsync("https://api.github.com/repos/sugumar247/SoundSync/releases/latest");
                using var doc = JsonDocument.Parse(responseJson);
                if (doc.RootElement.TryGetProperty("tag_name", out var tagProperty))
                {
                    string tag = tagProperty.GetString()?.Trim().TrimStart('v', 'V') ?? "";
                    if (Version.TryParse(tag, out var latestVersion))
                    {
                        var currentVersion = typeof(MainViewModel).Assembly.GetName().Version;
                        if (currentVersion != null && latestVersion > currentVersion)
                        {
                            if (doc.RootElement.TryGetProperty("html_url", out var urlProperty))
                            {
                                _latestReleaseUrl = urlProperty.GetString() ?? _latestReleaseUrl;
                            }
                            _dispatcher.Invoke(() =>
                            {
                                UpdateText = $"SoundSync 原版有新版本 v{tag} 可用（可能不含汉化）！";
                                IsUpdateBannerVisible = true;
                            });
                        }
                    }
                }
            }
            catch { }
        }

        private void DownloadUpdate()
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = _latestReleaseUrl,
                    UseShellExecute = true
                });
            }
            catch { }
        }

        private string GetLocalIPAddress()
        {
            try
            {
                using (Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0))
                {
                    socket.Connect("8.8.8.8", 65530);
                    IPEndPoint? endPoint = socket.LocalEndPoint as IPEndPoint;
                    return endPoint?.Address.ToString() ?? "127.0.0.1";
                }
            }
            catch
            {
                return "127.0.0.1";
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
