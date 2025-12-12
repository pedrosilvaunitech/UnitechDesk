using CommunicationLibrary.Communication;
using CommunicationLibrary.Models;
using HelpersLibrary.Helpers;
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Timers;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace ShareScreen
{
    public partial class ScreenSharingWindow : Window, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        private void NotifyPropertyChanged([CallerMemberName] string prop = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
        }

        Timer _mouseTimer = new Timer(500);
        Timer _keyboardTimer = new Timer(700);

        private BitmapImage _image;
        public BitmapImage Image
        {
            get => _image;
            set { _image = value; NotifyPropertyChanged(); }
        }

        private byte[] _imageData;
        public byte[] ImageData
        {
            get => _imageData;
            set
            {
                _imageData = value;
                NotifyPropertyChanged();

                Dispatcher.Invoke(() =>
                {
                    if (_imageData != null)
                        Image = ToImage(_imageData);
                });
            }
        }

        public float OriginalWidth { get; set; }
        public float OriginalHeight { get; set; }

        private readonly string _host;
        private bool _mouseUp;

        public ScreenSharingWindow(string host)
        {
            InitializeComponent();
            DataContext = this;

            _host = host;

            _mouseTimer.AutoReset = false;
            _keyboardTimer.AutoReset = false;
        }

        // =====================================================================
        // Converte byte[] -> BitmapImage
        // =====================================================================
        public BitmapImage ToImage(byte[] arr)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = new MemoryStream(arr);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch
            {
                return null;
            }
        }

        // =====================================================================
        // Converte coordenadas exibidas → resolução original
        // =====================================================================
        private System.Drawing.Point ConvertPosition(MouseEventArgs e)
        {
            if (ss.Source == null || OriginalWidth == 0 || OriginalHeight == 0)
                return new System.Drawing.Point(0, 0);

            double xFactor = OriginalWidth / ss.ActualWidth;
            double yFactor = OriginalHeight / ss.ActualHeight;

            var p = e.GetPosition(ss);

            return new System.Drawing.Point(
                (int)(p.X * xFactor),
                (int)(p.Y * yFactor)
            );
        }

        // =====================================================================
        // MOUSE MOVE
        // =====================================================================
        private void ImageHolder_MouseMove(object sender, MouseEventArgs e)
        {
            var p = ConvertPosition(e);
            Communicator.Instance.ProduceMouseMove(p.X, p.Y, _host);
        }

        // =====================================================================
        // MOUSE DOWN / CLICK / DOUBLE CLICK INTELIGENTE
        // =====================================================================
        private void ImageHolder_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _mouseUp = false;

            // Double Click
            if (e.ClickCount == 2)
            {
                Communicator.Instance.Produce(
                    new InputDataComm()
                    {
                        DataType = MessageTypeComm.MouseDoubleClick,
                        MouseData = e.ChangedButton.Map()
                    },
                    _host);
                return;
            }

            // Detecção se é click rápido ou hold
            Task.Run(() =>
            {
                System.Threading.Thread.Sleep(130);

                if (_mouseUp)
                {
                    Communicator.Instance.Produce(
                        new InputDataComm()
                        {
                            DataType = MessageTypeComm.MouseClick,
                            MouseData = e.ChangedButton.Map()
                        }, _host);
                }
                else
                {
                    Communicator.Instance.Produce(
                        new InputDataComm()
                        {
                            DataType = MessageTypeComm.MouseDown,
                            MouseData = e.ChangedButton.Map()
                        }, _host);
                }
            });
        }

        private void ImageHolder_MouseUp(object sender, MouseButtonEventArgs e)
        {
            _mouseUp = true;

            Communicator.Instance.Produce(
                new InputDataComm()
                {
                    DataType = MessageTypeComm.MouseUp,
                    MouseData = e.ChangedButton.Map()
                },
                _host);
        }

        // =====================================================================
        // TECLADO
        // =====================================================================
        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (!_keyboardTimer.Enabled)
                _keyboardTimer.Start();

            Communicator.Instance.Produce(
                new InputDataComm()
                {
                    DataType = MessageTypeComm.KeyboardDown,
                    KeyboardData = e.Key.Map()
                },
                _host);
        }

        private void Window_KeyUp(object sender, KeyEventArgs e)
        {
            Communicator.Instance.Produce(
                new InputDataComm()
                {
                    DataType = MessageTypeComm.KeyboardUp,
                    KeyboardData = e.Key.Map()
                },
                _host);
        }

        // =====================================================================
        private void Window_MouseEnter(object sender, MouseEventArgs e)
        {
            this.Focus();
            Keyboard.Focus(this);
        }

        private void Window_MouseLeave(object sender, MouseEventArgs e)
        {
            Keyboard.ClearFocus();
        }
    }
}
