using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Be.Windows.Forms;
using WebSocketSharp;
using System.Web.Script.Serialization;
using usb2snesmemoryviewer.Properties;

namespace usb2snes
{
    public partial class usb2snesmemoryviewer : Form
    {
        private const string SniWebSocketUrl = "ws://localhost:23074/";

        public usb2snesmemoryviewer()
        {
            InitializeComponent();

            Text = "usb2snesviewer - READ ONLY (SNI)";

            comboBoxRegion.Items.Add("SD2SNES_RANGE");
            comboBoxRegion.Items.Add("WRAM");
            comboBoxRegion.Items.Add("VRAM");
            comboBoxRegion.Items.Add("APU");
            comboBoxRegion.Items.Add("CGRAM");
            comboBoxRegion.Items.Add("OAM");
            comboBoxRegion.Items.Add("PPUREG");
            comboBoxRegion.Items.Add("CPUREG");
            comboBoxRegion.Items.Add("MISC");
            comboBoxRegion.Items.Add("MSU");
            comboBoxRegion.Items.Add("CMD");

            _waitHandles[0] = _ev;
            _waitHandles[1] = _term;

            try
            {
                _provider = new DynamicByteProvider(new byte[0x50000]);
                hexBox.ByteProvider = _provider;
                hexBox.ReadOnly = true;
                hexBox.Font = new Font("MonoSpace", 8);
            }
            catch (IOException x)
            {
                HandleException(x);
            }

            // Deliberately slower than the legacy 100 ms poll while we validate
            // SNI + FXPAK stability on real hardware.
            _timer.AutoReset = false;
            _timer.Interval = 500;
            _timer.Elapsed += RefreshSnesMemory;
            _timer.Stop();

            buttonGsuDebug.Visible = false;
            buttonSa1Debug.Visible = false;
            comboBoxRegion.SelectedIndex = 0;
        }

        ~usb2snesmemoryviewer()
        {
            try
            {
                _timer.Stop();
                _term.Set();
                if (_ws != null)
                    _ws.Close();
            }
            catch
            {
                // Destructors must not throw.
            }
        }

        private void comboBoxPort_SelectedIndexChanged(object sender, EventArgs e)
        {
            _timer.Stop();
            Monitor.Enter(_timerLock);

            try
            {
                hexBox.Enabled = false;

                if (comboBoxPort.SelectedIndex >= 0)
                {
                    Connect();

                    RequestType req = new RequestType();
                    req.Opcode = OpcodeType.Attach.ToString();
                    req.Space = "SNES";
                    req.Operands = new List<string>(new[] { comboBoxPort.SelectedItem.ToString() });
                    _ws.Send(serializer.Serialize(req));

                    // Attach has no success response in the usb2snes protocol.
                    // Give SNI a tiny moment before the first memory read.
                    Thread.Sleep(50);

                    pictureConnected.Image = Resources.bullet_green;
                    pictureConnected.Refresh();
                    toolStripStatusLabel1.Text = "connected (read only)";
                    Setup();
                }
            }
            catch (Exception x)
            {
                HandleException(x);
            }
            finally
            {
                Monitor.Exit(_timerLock);
                if (comboBoxPort.SelectedIndex >= 0)
                    _timer.Start();
            }
        }

        private void comboBoxRegion_SelectedIndexChanged(object sender, EventArgs e)
        {
            _timer.Stop();
            Monitor.Enter(_timerLock);

            try
            {
                int oldRegionSize = _regionSize;
                _offset = 0;
                _region = comboBoxRegion.SelectedIndex;

                switch (_region)
                {
                    case 0:
                        try { _regionBase = Convert.ToInt32(textBoxBase.Text, 16); }
                        catch { _regionBase = 0x0; }
                        try { _regionSize = Convert.ToInt32(textBoxSize.Text, 16); }
                        catch { _regionSize = 0x100; }
                        break;
                    case 1: _regionBase = 0xF50000; _regionSize = 0x0020000; break;
                    case 2: _regionBase = 0xF70000; _regionSize = 0x0010000; break;
                    case 3: _regionBase = 0xF80000; _regionSize = 0x0010000; break;
                    case 4: _regionBase = 0xF90000; _regionSize = 0x0000200; break;
                    case 5: _regionBase = 0xF90200; _regionSize = 0x0000220; break;
                    case 6: _regionBase = 0xF90500; _regionSize = 0x0000200; break;
                    case 7: _regionBase = 0xF90700; _regionSize = 0x0000200; break;
                    case 8: _regionBase = 0xF90420; _regionSize = 0x00000E0; break;
                    case 9: _regionBase = 0x000000; _regionSize = 0x0007800; break;
                    case 10: _regionBase = 0x002A00; _regionSize = 0x0000400; break;
                    default: _regionBase = 0xF50000; _regionSize = 0x0002000; break;
                }

                ResizeProvider(oldRegionSize, _regionSize);
                hexBox.ReadOnly = true;
            }
            catch (Exception x)
            {
                HandleException(x);
            }
            finally
            {
                Monitor.Exit(_timerLock);
                if (comboBoxPort.SelectedIndex >= 0)
                    _timer.Start();
            }
        }

        private void ResizeProvider(int oldRegionSize, int newRegionSize)
        {
            if (_provider == null || oldRegionSize == newRegionSize)
                return;

            if (oldRegionSize < newRegionSize)
                _provider.InsertBytes(oldRegionSize, new byte[newRegionSize - oldRegionSize]);
            else
                _provider.DeleteBytes(newRegionSize, oldRegionSize - newRegionSize);
        }

        private void Setup()
        {
            hexBox.Enabled = true;
            hexBox.ReadOnly = true;
            GetDataAndResetHead();
        }

        private void buttonRefresh_Click(object sender, EventArgs e)
        {
            _timer.Stop();

            comboBoxPort.Items.Clear();
            comboBoxPort.ResetText();
            comboBoxPort.SelectedIndex = -1;
            pictureConnected.Image = Resources.bullet_red;

            try
            {
                Connect();

                var req = new RequestType
                {
                    Opcode = OpcodeType.DeviceList.ToString(),
                    Space = "SNES"
                };

                _ws.Send(serializer.Serialize(req));
                if (WaitHandle.WaitAny(_waitHandles, 1000) != 0)
                    return;

                _ev.Reset();

                if (_rsp != null && _rsp.Results != null)
                {
                    foreach (var port in _rsp.Results)
                        comboBoxPort.Items.Add(port);
                }

                if (comboBoxPort.Items.Count != 0)
                    comboBoxPort.SelectedIndex = 0;
            }
            catch (Exception x)
            {
                HandleException(x);
            }
        }

        private void RefreshMemoryView()
        {
            hexBox.Refresh();
        }

        private void RefreshSnesMemory(object source, System.Timers.ElapsedEventArgs e)
        {
            if (!checkBoxAutoUpdate.Checked)
            {
                _timer.Start();
                return;
            }

            if (!Monitor.TryEnter(_timerLock))
                return;

            try
            {
                GetDataAndResetHead();
            }
            catch (Exception x)
            {
                try { BeginInvoke(new Action(() => HandleException(x))); }
                catch { }
            }
            finally
            {
                Monitor.Exit(_timerLock);
                _timer.Start();
            }

            try { BeginInvoke(new Action(RefreshMemoryView)); }
            catch { }
        }

        private void HandleException(Exception x)
        {
            toolStripStatusLabel1.Text = x.Message;
            try
            {
                if (_ws != null)
                    _ws.Close();
            }
            catch { }
            pictureConnected.Image = Resources.bullet_red;
        }

        private void GetDataAndResetHead()
        {
            if (_ws == null || _ws.ReadyState != WebSocketState.Open)
                return;

            _offset = 0;
            _ev.Reset();

            RequestType req = new RequestType();
            req.Opcode = OpcodeType.GetAddress.ToString();
            req.Space = _region == 10 ? "CMD" : _region == 9 ? "MSU" : "SNES";
            req.Operands = new List<string>(new[]
            {
                _regionBase.ToString("X"),
                _regionSize.ToString("X")
            });

            _ws.Send(serializer.Serialize(req));
            if (WaitHandle.WaitAny(_waitHandles, 2000) != 0)
                return;

            int count = Math.Min(_regionSize, _memory.Length);
            for (uint i = 0; i < count; i++)
                _provider.WriteByteNoEvent(i, _memory[i]);

            _ev.Reset();
        }

        private readonly byte[] _memory = new byte[0x1000000];
        private DynamicByteProvider _provider;

        private int _region = 0;
        private int _regionBase = 0;
        private int _regionSize = 0x50000;

        private readonly System.Timers.Timer _timer = new System.Timers.Timer();
        private readonly object _timerLock = new object();

        private void buttonExport_Click(object sender, EventArgs e)
        {
            using (FileStream fs = new FileStream(
                @"export-" + comboBoxRegion.SelectedItem + ".bin",
                FileMode.Create,
                FileAccess.Write))
            {
                fs.Write(_memory, 0, _regionSize);
            }
        }

        private void textBoxBase_TextChanged(object sender, EventArgs e)
        {
            if (comboBoxRegion.SelectedItem == null ||
                comboBoxRegion.SelectedItem.ToString() != "SD2SNES_RANGE")
                return;

            try { _regionBase = Convert.ToInt32(textBoxBase.Text, 16); }
            catch { }
        }

        private void textBoxSize_TextChanged(object sender, EventArgs e)
        {
            if (comboBoxRegion.SelectedItem == null ||
                comboBoxRegion.SelectedItem.ToString() != "SD2SNES_RANGE")
                return;

            _timer.Stop();
            Monitor.Enter(_timerLock);

            try
            {
                int oldRegionSize = _regionSize;
                _regionSize = Convert.ToInt32(textBoxSize.Text, 16);
                if (_regionSize <= 0)
                    _regionSize = 1;
                if (_regionSize > _memory.Length)
                    _regionSize = _memory.Length;

                ResizeProvider(oldRegionSize, _regionSize);
                hexBox.ReadOnly = true;
            }
            catch
            {
                // Ignore incomplete hex input while the user is typing.
            }
            finally
            {
                Monitor.Exit(_timerLock);
                if (comboBoxPort.SelectedIndex >= 0)
                    _timer.Start();
            }
        }

        private void pictureConnected_Click(object sender, EventArgs e)
        {
            _timer.Stop();
            try
            {
                if (_ws != null)
                    _ws.Close();
            }
            catch { }
            pictureConnected.Image = Resources.bullet_red;
            pictureConnected.Refresh();
            toolStripStatusLabel1.Text = "disconnected";
        }

        private void ws_Opened(object sender, EventArgs e)
        {
        }

        private void ws_MessageReceived(object sender, MessageEventArgs e)
        {
            if (e.Type == Opcode.Text)
            {
                _rsp = serializer.Deserialize<ResponseType>(e.Data);
                _ev.Set();
                return;
            }

            if (e.Type != Opcode.Binary)
                return;

            int remaining = _memory.Length - _offset;
            int copyLength = Math.Min(e.RawData.Length, remaining);
            if (copyLength <= 0)
                return;

            Array.Copy(e.RawData, 0, _memory, _offset, copyLength);
            _offset += copyLength;

            if (_offset >= _regionSize)
            {
                _offset = 0;
                _ev.Set();
            }
        }

        private void ws_Error(object sender, EventArgs e)
        {
        }

        private void ws_Closed(object sender, EventArgs e)
        {
        }

        private void Connect()
        {
            _offset = 0;
            _ev.Reset();

            if (_ws != null && _ws.ReadyState == WebSocketState.Open)
                _ws.Close();

            _ws = new WebSocket(SniWebSocketUrl);
            _ws.Log.Output = (_, __) => { };
            _ws.OnOpen += ws_Opened;
            _ws.OnMessage += ws_MessageReceived;
            _ws.OnClose += ws_Closed;
            _ws.OnError += ws_Error;
            _ws.WaitTime = TimeSpan.FromSeconds(4);
            _ws.Connect();

            if (_ws.ReadyState != WebSocketState.Open &&
                _ws.ReadyState != WebSocketState.Connecting)
                throw new Exception("Connection timeout");

            RequestType req = new RequestType
            {
                Opcode = OpcodeType.Name.ToString(),
                Space = "SNES",
                Operands = new List<string>(new[] { "MemoryViewer Read Only" })
            };
            _ws.Send(serializer.Serialize(req));
            _ev.Reset();
        }

        private WebSocket _ws = new WebSocket(SniWebSocketUrl);
        private ResponseType _rsp = new ResponseType();
        private readonly ManualResetEvent _ev = new ManualResetEvent(false);
        private readonly ManualResetEvent _term = new ManualResetEvent(false);
        private readonly JavaScriptSerializer serializer = new JavaScriptSerializer();
        private int _offset = 0;
        private readonly WaitHandle[] _waitHandles = new WaitHandle[2];

        private void buttonGsuDebug_Click(object sender, EventArgs e)
        {
            toolStripStatusLabel1.Text = "GSU debug disabled in read-only build";
        }

        private void buttonSa1Debug_Click(object sender, EventArgs e)
        {
            toolStripStatusLabel1.Text = "SA-1 debug disabled in read-only build";
        }

        // Kept only so the legacy hidden debug forms still compile. This build
        // intentionally performs no configuration/register writes.
        public void GSUReg(int[] regs)
        {
            toolStripStatusLabel1.Text = "write operation blocked (read only)";
        }

        public void GSUUpdate()
        {
            Monitor.Enter(_timerLock);
            try
            {
                GetDataAndResetHead();
            }
            finally
            {
                Monitor.Exit(_timerLock);
            }
        }
    }
}
