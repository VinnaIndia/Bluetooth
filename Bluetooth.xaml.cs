#if ANDROID
using Android.Bluetooth;
using Android.Content;
#endif

namespace Bluetooth;

public partial class Bluetooth : ContentPage
{
#if ANDROID
    BluetoothService _bt = new();
    CancellationTokenSource? _cts;
    List<BluetoothDevice> _pairedDevices = new();
    byte[]? _pickedImageBytes;

    public Bluetooth()
    {
        InitializeComponent();
        _bt.StatusChanged += status =>
        {
            ReceivingStatusLabel.Text = $"Status: {status}";
            ReceivingStatusLabel.TextColor =
                status.Contains("✓") || status.Contains("done") ||
                status.Contains("connected")
                    ? Colors.Green : Colors.Orange;
        };
        _bt.TextReceived += text =>
        {
            ReceivedTextLabel.Text = text;
            ReceivedTextLabel.TextColor = Colors.Black;
        };

        _bt.ImageReceived += bytes =>
        {
            ReceivedImageBorder.IsVisible = true;
            ReceivedImage.Source = ImageSource.FromStream(
                () => new MemoryStream(bytes));
        };
    }

    // ── RECEIVER ────────────────────────────────────────────────────

    async void OnStartReceiving(object sender, EventArgs e)
    {
        await RequestPermissionsAsync();

        _cts?.Cancel();
        _cts = new CancellationTokenSource();

        ReceivingStatusLabel.Text = "Status: listening for incoming data...";
        ReceivingStatusLabel.TextColor = Colors.Green;
        StartReceivingButton.Text = "Listening... (tap to restart)";

        try
        {
            await _bt.StartListeningAsync(_cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ReceivingStatusLabel.Text = $"Error: {ex.Message}";
            ReceivingStatusLabel.TextColor = Colors.Red;
        }
    }

    // ── SENDER ──────────────────────────────────────────────────────

    async void OnScanDevices(object sender, EventArgs e)
    {
        await RequestPermissionsAsync();

        // Use BluetoothManager instead of deprecated DefaultAdapter
        var btManager = Platform.CurrentActivity?
            .GetSystemService(Context.BluetoothService) as BluetoothManager;
        var adapter = btManager?.Adapter;

        if (adapter == null || !adapter.IsEnabled)
        {
            await DisplayAlert("Bluetooth Off",
                "Please enable Bluetooth in Settings.", "OK");
            return;
        }

        _pairedDevices = adapter.BondedDevices?.ToList()
                         ?? new List<BluetoothDevice>();

        if (_pairedDevices.Count == 0)
        {
            await DisplayAlert("No Paired Devices",
                "Go to Android Settings → Bluetooth and pair with " +
                "the other device first.", "OK");
            return;
        }

        DevicePicker.ItemsSource = _pairedDevices
            .Select(d => d.Name ?? "Unknown").ToList();

        ConnectStatusLabel.Text =
            $"{_pairedDevices.Count} paired device(s) found. Select one.";
        ConnectStatusLabel.TextColor = Colors.Gray;
    }

    async void OnDeviceSelected(object sender, EventArgs e)
    {
        if (DevicePicker.SelectedIndex < 0) return;

        var device = _pairedDevices[DevicePicker.SelectedIndex];
        ConnectStatusLabel.Text = $"Connecting to {device.Name}...";
        ConnectStatusLabel.TextColor = Colors.Orange;

        try
        {
            await _bt.ConnectToDeviceAsync(device);
            ConnectStatusLabel.Text = $"Connected to {device.Name} ✓";
            ConnectStatusLabel.TextColor = Colors.Green;
        }
        catch (Exception ex)
        {
            ConnectStatusLabel.Text = $"❌ {ex.Message}";
            ConnectStatusLabel.TextColor = Colors.Red;

            await DisplayAlert(
                "Connection Failed",
                "Please check:\n\n" +
                "1. Phone B has tapped 'Start Receiving'\n" +
                "2. Bluetooth is ON on both phones\n" +
                "3. Both phones are within 1 metre range\n" +
                "4. App has Bluetooth permission\n\n" +
                $"Error: {ex.Message}",
                "OK");
        }
    }

    async void OnSendText(object sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(MessageEntry.Text))
        {
            await DisplayAlert("Empty", "Please type a message first.", "OK");
            return;
        }

        try
        {
            SendStatusLabel.Text = "Sending...";
            SendStatusLabel.TextColor = Colors.Orange;

            await _bt.SendTextAsync(MessageEntry.Text);

            SendStatusLabel.Text = "Text sent successfully ✓";
            SendStatusLabel.TextColor = Colors.Green;
            MessageEntry.Text = string.Empty;
        }
        catch (Exception ex)
        {
            SendStatusLabel.Text = $"Failed: {ex.Message}";
            SendStatusLabel.TextColor = Colors.Red;
        }
    }

    async void OnPickImage(object sender, EventArgs e)
    {
        try
        {
            var result = await FilePicker.PickAsync(new PickOptions
            {
                FileTypes = FilePickerFileType.Images,
                PickerTitle = "Select an image to send"
            });

            if (result == null) return;

            await using var stream = await result.OpenReadAsync();
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            _pickedImageBytes = ms.ToArray();

            SelectedImagePreview.IsVisible = true;
            SelectedImagePreview.Source = ImageSource.FromStream(
                () => new MemoryStream(_pickedImageBytes));

            SendImageButton.IsEnabled = true;
            SendStatusLabel.Text = "Image ready to send.";
            SendStatusLabel.TextColor = Colors.Gray;
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", ex.Message, "OK");
        }
    }

    async void OnSendImage(object sender, EventArgs e)
    {
        if (_pickedImageBytes == null) return;

        try
        {
            SendStatusLabel.Text = "Sending image...";
            SendStatusLabel.TextColor = Colors.Orange;

            await _bt.SendImageAsync(_pickedImageBytes);

            SendStatusLabel.Text = "Image sent successfully ✓";
            SendStatusLabel.TextColor = Colors.Green;
        }
        catch (Exception ex)
        {
            SendStatusLabel.Text = $"Failed: {ex.Message}";
            SendStatusLabel.TextColor = Colors.Red;
        }
    }

    // ── PERMISSIONS ─────────────────────────────────────────────────

    async Task RequestPermissionsAsync()
    {
        await Permissions.RequestAsync<Permissions.Bluetooth>();
        await Permissions.RequestAsync<Permissions.LocationWhenInUse>();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _cts?.Cancel();
        _bt.Dispose();
    }

#else
    // Non-Android stub so the page still compiles on iOS/Windows
    public Bluetooth()
    {
        InitializeComponent();
    }

    void OnStartReceiving(object sender, EventArgs e) { }
    void OnScanDevices(object sender, EventArgs e) { }
    void OnDeviceSelected(object sender, EventArgs e) { }
    void OnSendText(object sender, EventArgs e) { }
    void OnPickImage(object sender, EventArgs e) { }
    void OnSendImage(object sender, EventArgs e) { }
#endif
}