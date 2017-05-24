using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.ApplicationModel.Core;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Media.Capture;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Playback;
using Windows.Networking;
using Windows.Networking.Connectivity;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

// The Blank Page item template is documented at http://go.microsoft.com/fwlink/?LinkId=402352&clcid=0x409

namespace StreamSocketApp
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainPage : Page
    {
        private MediaPlaybackList _playlist = null;
        private bool Buffering => _playlist.Items.Count == 0;
        MediaCapture mediaCapture;
        string serviceNameForConnect = "22112";
        string hostNameForConnect = "localhost";
        NetworkAdapter adapter = null;
        StreamSocket clientSocket = null;

        public MainPage()
        {
            this.InitializeComponent();

            _playlist = new MediaPlaybackList();
            //remove played items from the list
            _playlist.CurrentItemChanged += (sender, args) => _playlist.Items.Remove(args.OldItem);
            //_playlist.ItemOpened += Playlist_ItemOpened;
            //_playlist.ItemFailed += Playlist_ItemFailed;
            //playbackElement.AutoPlay = true;
            playbackElement.SetPlaybackSource(_playlist);
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

        private async void SendHello_Click(object sender, RoutedEventArgs e)
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

                await mediaCapture.StartRecordToStreamAsync(MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Auto), memoryStream);

                await Task.Delay(TimeSpan.FromMilliseconds(16.67));

                await mediaCapture.StopRecordAsync();

                //create a CurrentVideo object to hold stream data and give it a unique id
                //which the client app can use to ensure they only request each video once
                memoryStream.Seek(0);

                // Write first the length of the string as UINT32 value followed up by the string. 
                // Writing data to the writer will just store data in memory.   
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
            await Task.WhenAll(PlayNextVideo(), DownloadVideos(args));            
        }

        public async Task DownloadVideos(StreamSocketListenerConnectionReceivedEventArgs args)
        {
            DataReader reader = new DataReader(args.Socket.InputStream);

            try
            {
                uint lastStringLength = 0;

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

                    if (lastStringLength == stringLength)
                    {
                        // The underlying socket was closed before we were able to read the whole data.
                        continue;
                    }

                    NotifyUserFromAsyncThread(reader.ReadBuffer(actualStringLength));

                    lastStringLength = stringLength;
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
                    var source = MediaSource.CreateFromStream(buffer.AsStream().AsRandomAccessStream(), "video/mp4");

                    var item = new MediaPlaybackItem(source);

                    _playlist.Items.Add(item);

                    Debug.WriteLine(_playlist.Items.Count);

                    if (_playlist.Items.Count > 2)
                    {
                        //this is a bug I haven't worked out yet..
                        //from time to time, the list seems to get stuck ?!?
                        //Debug.WriteLine("Playlist stuck, moving next");
                        //we can get things moving again by forcing this.. 
                        //TODO: Find out why and implement a more robust solution
                        _playlist.MoveNext();
                    }

                    if (playbackElement.CurrentState != MediaElementState.Playing)
                    {
                        //if this is the first cycle/video and we've not yet started playing, or
                        //if the network is slow, the MediaElement may have reached the end of 
                        //the previous item and stopped, putting us into a state of "buffering"...
                        //Debug.WriteLine("Playing...");
                        playbackElement.Play();
                    }
                });
        }

        private async Task PlayNextVideo()
        {
            while (true)
            {
                if (!Buffering)
                {

                    //as long as there's at least one item in the
                    //playlist, start playing the MediaElement
                    //BufferingLbl.Visibility = Visibility.Collapsed;
                    playbackElement.Play();
                    break;
                }
                else
                {
                    //else go into a 'buffering' loop
                    //BufferingLbl.Visibility = Visibility.Visible;
                    await Task.Delay(500);
                }
            }
        }
    }
}
