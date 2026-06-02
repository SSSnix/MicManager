using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace MicManager
{
    public partial class MainWindow : Window
    {
        private MMDeviceEnumerator _enumerator;
        private MMDevice _currentDevice;
        private WasapiCapture _capture;
        private BufferedWaveProvider _bufferedWaveProvider;
        private WaveToSampleProvider _sampleConverter;
        private VolumeSampleProvider _volumeProvider;
        private WasapiOut _output;
        private bool _isTesting = false;
        private DispatcherTimer _vuTimer;
        private float _currentLevel = 0f;
        
        private static NotifyIcon _trayIcon;

        public MainWindow()
        {
            InitializeComponent();
            
            InitTray();
            
            Loaded += OnLoaded;
            Closing += OnClosing;
            MonitorVolumeSlider.ValueChanged += (s, e) => 
                MonitorVolLabel.Text = $"{Math.Round(MonitorVolumeSlider.Value * 100)}%";
        }

        private void InitTray()
        {
            try
            {
                Debug.WriteLine("[Tray] Creating NotifyIcon...");
                
                _trayIcon = new NotifyIcon
                {
                    Text = "Mic Manager",
                    Visible = true,
                    Icon = System.Drawing.SystemIcons.Application
                };
                
                _trayIcon.MouseClick += (s, e) => 
                { 
                    Debug.WriteLine($"[Tray] Clicked: {e.Button}");
                    if (e.Button == MouseButtons.Left) ToggleVisibility(); 
                };
                
                var menu = new ContextMenuStrip();
                menu.Items.Add("Открыть", null, (s, e) => ToggleVisibility());
                menu.Items.Add("Выход", null, (s, e) => 
                { 
                    Debug.WriteLine("[Tray] Exit clicked");
                    _trayIcon.Visible = false;
                    _trayIcon.Dispose();
                    Application.Current.Shutdown(); 
                });
                _trayIcon.ContextMenuStrip = menu;
                
                Debug.WriteLine("[Tray] NotifyIcon created successfully");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Tray] ERROR: {ex.Message}");
                MessageBox.Show($"Не удалось создать иконку в трее:\n{ex.Message}\n\nПриложение будет работать в оконном режиме.", 
                    "Mic Manager", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Debug.WriteLine("[App] Window loaded");
            _enumerator = new MMDeviceEnumerator();
            RefreshDevices();
            PositionWindow();
            
            if (_trayIcon?.Visible == true)
            {
                Debug.WriteLine("[App] Hiding window, tray is active");
                this.Hide();
            }
            else
            {
                Debug.WriteLine("[App] Keeping window visible (tray failed)");
            }
        }

        private void ToggleVisibility()
        {
            Debug.WriteLine($"[App] ToggleVisibility: IsVisible={IsVisible}");
            if (IsVisible) 
                Hide();
            else 
            { 
                PositionWindow(); 
                Show(); 
                Activate(); 
            }
        }

        private void PositionWindow()
        {
            Left = SystemParameters.WorkArea.Right - ActualWidth - 20;
            Top = SystemParameters.WorkArea.Bottom - ActualHeight - 60;
        }

        private void RefreshDevices()
        {
            DeviceCombo.Items.Clear();
            var devices = _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
            foreach (var dev in devices)
            {
                var item = new System.Windows.Controls.ComboBoxItem 
                { 
                    Content = dev.FriendlyName, 
                    Tag = dev,
                    Background = System.Windows.Media.Brushes.White,
                    Foreground = System.Windows.Media.Brushes.Black
                };
                DeviceCombo.Items.Add(item);
            }

            try
            {
                var def = _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
                var item = DeviceCombo.Items.Cast<System.Windows.Controls.ComboBoxItem>()
                    .FirstOrDefault(i => ((MMDevice)i.Tag).FriendlyName == def.FriendlyName);
                if (item != null) DeviceCombo.SelectedItem = item;
            }
            catch { }
        }

        private void DeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isTesting) StopTest();
            _currentDevice = (sender as System.Windows.Controls.ComboBox)?.SelectedItem is System.Windows.Controls.ComboBoxItem item ? item.Tag as MMDevice : null;
            if (_currentDevice != null)
            {
                VolumeSlider.Value = _currentDevice.AudioEndpointVolume.MasterVolumeLevelScalar;
                MuteToggle.IsChecked = _currentDevice.AudioEndpointVolume.Mute;
            }
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_currentDevice == null) return;
            float vol = Math.Clamp((float)e.NewValue, 0f, 1f);
            _currentDevice.AudioEndpointVolume.MasterVolumeLevelScalar = vol;
            VolumeLabel.Text = $"{Math.Round(vol * 100)}%";
        }

        private void MuteCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_currentDevice == null) return;
            _currentDevice.AudioEndpointVolume.Mute = MuteToggle.IsChecked == true;
        }

        private void MonitorCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (!_isTesting) return;
            UpdateMonitoringState();
        }

        private void TestBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!_isTesting) StartTest(); else StopTest();
        }

        private void StartTest()
        {
            if (_currentDevice == null) return;
            try
            {
                TestBtn.Content = "Остановить";
                TestBtn.Background = System.Windows.Media.Brushes.OrangeRed;
                _isTesting = true;

                _capture = new WasapiCapture(_currentDevice);
                _capture.DataAvailable += OnDataAvailable;
                _capture.RecordingStopped += OnRecordingStopped;

                _bufferedWaveProvider = new BufferedWaveProvider(_capture.WaveFormat) { DiscardOnBufferOverflow = true };
                _sampleConverter = new WaveToSampleProvider(_bufferedWaveProvider);
                _volumeProvider = new VolumeSampleProvider(_sampleConverter) { Volume = (float)MonitorVolumeSlider.Value };

                if (MonitorToggle.IsChecked == true)
                {
                    _output = new WasapiOut();
                    _output.Init(_volumeProvider);
                    _output.Play();
                }

                _capture.StartRecording();
                _vuTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
                _vuTimer.Tick += (s, args) => { LevelMeter.Value = _currentLevel; _currentLevel *= 0.92f; };
                _vuTimer.Start();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка: {ex.Message}", "Mic Manager", MessageBoxButton.OK, MessageBoxImage.Error);
                StopTest();
            }
        }

        private void StopTest()
        {
            _isTesting = false;
            _vuTimer?.Stop();
            TestBtn.Content = "Проверить микрофон";
            TestBtn.Background = System.Windows.Media.Brushes.Orange;
            LevelMeter.Value = 0;

            _capture?.StopRecording(); _capture?.Dispose(); _capture = null;
            _output?.Stop(); _output?.Dispose(); _output = null;
            _volumeProvider = null; _sampleConverter = null; _bufferedWaveProvider = null;
        }

        private void OnDataAvailable(object sender, WaveInEventArgs e)
        {
            if (_bufferedWaveProvider == null) return;
            _bufferedWaveProvider.AddSamples(e.Buffer, 0, e.BytesRecorded);

            float peak = 0f;
            if (_capture.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat)
            {
                var buffer = new float[e.BytesRecorded / 4];
                Buffer.BlockCopy(e.Buffer, 0, buffer, 0, e.BytesRecorded);
                foreach (var s in buffer) if (Math.Abs(s) > peak) peak = Math.Abs(s);
            }
            else
            {
                for (int i = 0; i < e.BytesRecorded; i += 2)
                {
                    float val = Math.Abs(BitConverter.ToInt16(e.Buffer, i)) / 32768f;
                    if (val > peak) peak = val;
                }
            }
            _currentLevel = Math.Max(_currentLevel * 0.85f, peak);
        }

        private void OnRecordingStopped(object sender, StoppedEventArgs e)
        {
            if (_isTesting) Dispatcher.Invoke(StopTest);
        }

        private void UpdateMonitoringState()
        {
            if (MonitorToggle.IsChecked == true && _output == null)
            {
                _output = new WasapiOut();
                _output.Init(_volumeProvider);
                _output.Play();
            }
            else if (MonitorToggle.IsChecked == false && _output != null)
            {
                _output.Stop(); _output.Dispose(); _output = null;
            }
        }

        private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            Debug.WriteLine("[App] Closing event");
            e.Cancel = true;
            Hide();
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Hide();
        private void Minimize_Click(object sender, RoutedEventArgs e) => Hide();
        private void Window_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ChangedButton == System.Windows.Input.MouseButton.Left) DragMove();
        }
    }
}