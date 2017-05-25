using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.ApplicationModel.Core;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Networking;
using Windows.Networking.Connectivity;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

// The Blank Page item template is documented at http://go.microsoft.com/fwlink/?LinkId=402352&clcid=0x409

namespace StreamSocketApp
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainPage : Page
    {
        MediaCapture mediaCapture;
        string serviceNameForConnect = "22112";
        string hostNameForConnect = "localhost";
        NetworkAdapter adapter = null;
        StreamSocket clientSocket = null;

        public MainPage()
        {
            this.InitializeComponent();            
        }

        private async void StartListener_Click(object sender, RoutedEventArgs e)
        {
            StreamSocketListener listener = new StreamSocketListener();

            listener.ConnectionReceived += OnConnection;

            await listener.BindServiceNameAsync(serviceNameForConnect);
        }

        private async void ConnectSocket_Click(object sender, RoutedEventArgs e)
        {
            // By default 'HostNameForConnect' is disabled and host name validation is not required. When enabling the
            // text box validating the host name is required since it was received from an untrusted source 
            // (user input). The host name is validated by catching ArgumentExceptions thrown by the HostName 
            // constructor for invalid input.
            // Note that when enabling the text box users may provide names for hosts on the Internet that require the
            // "Internet (Client)" capability.
            HostName hostName;

            mediaCapture = new MediaCapture();

            await mediaCapture.InitializeAsync();

            try
            {
                hostName = new HostName(hostNameForConnect);
            }
            catch (ArgumentException ex)
            {
                return;
            }

            clientSocket = new StreamSocket();

            // Save the socket, so subsequent steps can use it.
            try
            {
                if (adapter == null)
                {
                    // Connect to the server (in our case the listener we created in previous step).
                    await clientSocket.ConnectAsync(hostName, serviceNameForConnect);
                }
                else
                {
                    // Connect to the server (in our case the listener we created in previous step)
                    // limiting traffic to the same adapter that the user specified in the previous step.
                    // This option will be overriden by interfaces with weak-host or forwarding modes enabled.
                    //await socket.ConnectAsync(
                    //    hostName,
                    //    ServiceNameForConnect.Text,
                    //    SocketProtectionLevel.PlainSocket,
                    //    adapter);
                }
            }
            catch (Exception exception)
            {
                // If this is an unknown status it means that the error is fatal and retry will likely fail.
                if (SocketError.GetStatus(exception.HResult) == SocketErrorStatus.Unknown)
                {
                    throw;
                }
            }
        }

        private async void Send_Click(object sender, RoutedEventArgs e)
        {
            object outValue;
            // Create a DataWriter if we did not create one yet. Otherwise use one that is already cached.
            DataWriter writer;

            if (!CoreApplication.Properties.TryGetValue("clientDataWriter", out outValue))
            {
                writer = new DataWriter(clientSocket.OutputStream);

                CoreApplication.Properties.Add("clientDataWriter", writer);
            }
            else
            {
                writer = (DataWriter)outValue;
            }

            while (true)
            {
                var memoryStream = new InMemoryRandomAccessStream();

                await mediaCapture.CapturePhotoToStreamAsync(ImageEncodingProperties.CreateJpeg(), memoryStream);

                await Task.Delay(TimeSpan.FromMilliseconds(18.288)); //60 fps
                
                memoryStream.Seek(0);

                writer.WriteUInt32((uint)memoryStream.Size);

                writer.WriteBuffer(await memoryStream.ReadAsync(new byte[memoryStream.Size].AsBuffer(), (uint)memoryStream.Size, InputStreamOptions.None));

                // Write the locally buffered data to the network.
                try
                {
                    await writer.StoreAsync();
                }
                catch (Exception exception)
                {
                    // If this is an unknown status it means that the error if fatal and retry will likely fail.
                    if (SocketError.GetStatus(exception.HResult) == SocketErrorStatus.Unknown)
                    {
                        throw;
                    }
                }
            }
        }

        private async void OnConnection(StreamSocketListener sender, StreamSocketListenerConnectionReceivedEventArgs args)
        {
            await Task.WhenAll(DownloadVideos(args));            
        }

        public async Task DownloadVideos(StreamSocketListenerConnectionReceivedEventArgs args)
        {
            DataReader reader = new DataReader(args.Socket.InputStream);

            try
            {
                while (true)
                {
                    // Read first 4 bytes (length of the subsequent string).
                    uint sizeFieldCount = await reader.LoadAsync(sizeof(uint));

                    if (sizeFieldCount != sizeof(uint))
                    {
                        // The underlying socket was closed before we were able to read the whole data.
                        return;
                    }

                    // Read the string.
                    uint stringLength = reader.ReadUInt32();

                    uint actualStringLength = await reader.LoadAsync(stringLength);

                    if (stringLength != actualStringLength)
                    {
                        // The underlying socket was closed before we were able to read the whole data.
                        return;
                    }

                    NotifyUserFromAsyncThread(reader.ReadBuffer(actualStringLength));
                }
            }
            catch (Exception exception)
            {
                // If this is an unknown status it means that the error is fatal and retry will likely fail.
                if (SocketError.GetStatus(exception.HResult) == SocketErrorStatus.Unknown)
                {
                    throw;
                }
            }
        }

        private void NotifyUserFromAsyncThread(IBuffer buffer)
        {
            var ignore = Dispatcher.RunAsync(
                CoreDispatcherPriority.Normal, () =>
                {
                    Stream stream = buffer.AsStream();

                    byte[] imageBytes = new byte[stream.Length];

                    stream.ReadAsync(imageBytes, 0, imageBytes.Length);                    

                    InMemoryRandomAccessStream ms = new InMemoryRandomAccessStream();

                    ms.AsStreamForWrite().Write(imageBytes, 0, imageBytes.Length);

                    ms.Seek(0);

                    var image = new BitmapImage();

                    image.SetSource(ms);

                    ImageSource src = image;

                    imageElement.Source = src;
                });
        }
    }
}
