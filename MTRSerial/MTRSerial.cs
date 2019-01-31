using System;
using System.Diagnostics;
using System.Globalization;
using System.IO.Ports;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using MTRSerial.Enumerations;
using MTRSerial.ValueObjects;

namespace MTRSerial
{
    //for listening to reads and writes
    public class MTRCommandEventArgs : EventArgs
    {
        public MTRCommandEventArgs()
        {
            var timeStampString = (DateTime.Now.Ticks / DefaultValues.SystemTickDivider).ToString();
            var startIndex = Math.Max(0, timeStampString.Length - 8);
            TimeStamp = timeStampString.Substring(startIndex);
        }

        public string TimeStamp { get; set; }
        public string Identifier { get; set; }
        public string Command { get; set; }
        public string DebugText { get; set; }
        public string Data { get; set; }
    }

    public class MTRSerialPort
    {
        //for listening to reads and writes
        public event EventHandler<EventArgs> PerimeterCommunicationFailure;
        public event EventHandler<MTRCommandEventArgs> MTRCommunication;
        public event EventHandler<EventArgs> CommsErrorCountChanged;
        public event EventHandler<EventArgs> SerialPortOpened;
        public event EventHandler<EventArgs> SerialPortClosed;
        public event EventHandler<EventArgs> HardwareErrorOccurred;
        public event EventHandler<EventArgs> KeyPressed;


        private volatile int _communicationErrorsCount;               // Counter for communication errors
        private volatile bool _waitAck;
        private volatile bool _stsArmed;
        private DateTime _powerUpTime_utc;
        private readonly object _commLock = new object();
        private long _lastKeyPressFromTestStart_ms;
        private long _lastStsAcknowledgedMs;
        private long _lastAcknowledged_ms = 0;
        private SerialPort _serialPort;
        private volatile bool _waitingCommunicationCheckAck;
        private readonly TimerCallback _timerCallbackDelegate;
        private long _lastCommandSent;
        
        public MTRSerialPort()
        {
            _timerCallbackDelegate = KeepAliveTimerElapsed;
        }

        public DateTime GetPowerUpTime_utc => _powerUpTime_utc.ToUniversalTime();

        public void AskFromMTR(CommandsToMTR.CommandName command, byte[] binary = null)
        {
            var cmd = GetCommand(command, binary);
            SendData(cmd);
        }

        private string GetCommand(CommandsToMTR.CommandName command, byte[] binary = null)
        {
            if(command == CommandsToMTR.CommandName.SpoolBinary || command == CommandsToMTR.CommandName.GetMessageBinary && binary == null) throw new Exception("Binary content missing.")
            switch (command)
            {
                case CommandsToMTR.CommandName.Status: return "/ST";
                case CommandsToMTR.CommandName.Spool: return "/SA";
                case CommandsToMTR.CommandName.SpoolBinary: return "/SB" + binary;
                case CommandsToMTR.CommandName.NewSession: return "/NS";
                case CommandsToMTR.CommandName.GetMessageBinary: return "/GB" + binary;
                case CommandsToMTR.CommandName.SetClock: return "/SC" + binary;
                case CommandsToMTR.CommandName.ClearRingbuffer: return "/CL";
                default: return string.Empty;
            }
        }

        public virtual void SendData(string cmd)
        {
            if(string.IsNullOrEmpty(cmd)) return;
            if(cmd.Contains(@"/NS"))
            {
                _powerUpTime_utc = DateTime.UtcNow;
            }

            WaitForReply(cmd);

            _waitAck = true;
            if(cmd.Contains('T'))
            {
                _stsArmed = true;
            }
            try
            {
                _serialPort.Write(cmd + @"\r");
            }
            catch(Exception ex)
            {
                if(MTRCommunication != null)
                {
                    var infoArgs = CreateInfoArgs(@"Serial port data sending failed for the command beneath this line, check exception from application log");
                    MTRCommunication(this, infoArgs);
                }
                SerialPortErrorReceived(this, null);
                // TODO some logging
                //Logger.AddMessageToLogQueue(@"Serial port data sending failed");
                //Logger.FlushLogQueue();
                return;
            }
            _lastCommandSent = DateTime.Now.Ticks / DefaultValues.SystemTickDivider;
            if(MTRCommunication != null)
            {
                var command = cmd.Substring(0, 1);
                var data = string.Empty;
                if(cmd.Length > 1) data = cmd.Substring(1);
                var eventArgs = new MTRCommandEventArgs { Command = command, Data = data, Identifier = @"OUT", DebugText = "debug" };
                MTRCommunication(this, eventArgs);
            }
        }

        public bool CloseAndReopenSerialPort()
        {
            CloseSerialPort();
            Thread.Sleep(500);// The best practice for any application is to wait for some amount of time after calling the Close method before attempting to call the Open method, as the port may not be closed instantly. From:https://msdn.microsoft.com/en-us/library/system.io.ports.serialport.close.aspx
            return OpenSerialPort();
        }

        public void CloseSerialPort()
        {
            SendData(@"R0");
            WaitForReply(@"R0");

            if(_serialPort != null && _serialPort.IsOpen)
            {
                try
                {
                    _serialPort.Close();
                    if(MTRCommunication != null)
                    {
                        var infoArgs = CreateInfoArgs(@"Serial port closed");
                        MTRCommunication(this, infoArgs);
                    }
                }
                catch(Exception ex)
                {
                    if(MTRCommunication != null)
                    {
                        var infoArgs = CreateInfoArgs(@"Serial port closing failed, check exception from application log");
                        MTRCommunication(this, infoArgs);
                    }
                    // TODO Some logging
                    //Logger.AddMessageToLogQueue(@"Serial port closing failed");
                    //Logger.FlushLogQueue();
                    return;
                }
            }
            if(SerialPortClosed != null)
            {
                SerialPortClosed(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Opens the serial (COM) port
        /// </summary>
        public bool OpenSerialPort()
        {
            // Init the serial port and communications log
            var success = InitSerialPort();
            if(_serialPort != null && success)
            {
                MTRResponseData.Reset();
                ResetCommunicationErrorsCount();
                _lastKeyPressFromTestStart_ms = 0;
                if(SerialPortOpened != null)
                {
                    SerialPortOpened(this, EventArgs.Empty);
                }
                if(MTRCommunication != null)
                {
                    var infoArgs = CreateInfoArgs(@"Serial port opened");
                    MTRCommunication(this, infoArgs);
                }
            }
            return success;
        }

        public int GetCommunicationErrorsCount()
        {
            return _communicationErrorsCount;
        }
        
        public void ResetCommunicationErrorsCount()
        {
            _communicationErrorsCount = 0;
            if(CommsErrorCountChanged != null)
            {
                CommsErrorCountChanged(this, EventArgs.Empty);
            }
        }

        public bool SerialPortOpen()
        {
            return _serialPort != null && _serialPort.IsOpen;
        }

        public void DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            if(string.IsNullOrEmpty(Thread.CurrentThread.Name))
            {
                Thread.CurrentThread.Name = "SerialPortRXThread";
                Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
            }
            lock(_commLock)
            {
                var threw = false;
                while(!threw)
                {
                    try
                    {
                        ParseRxString(_serialPort.ReadLine());
                    }
                    catch
                    {
                        threw = true;
                    }
                }
            }
        }

        /// <summary>
        /// No operation, i.e. ping.
        /// The device just responds with an ack in any mode.
        /// This can be used by the PC to keep the Device in Active mode, if nothing is happening.
        /// </summary>
        /// <returns><c>False</c> if  there isnt connection after 20 tries.<c>True</c> if there is a connection</returns>
        public bool EnsureCommunicationToPerimeter()
        {
            _waitingCommunicationCheckAck = true;
            SendData(@"/ST"); 
            int i = 0;
            while(i < 20 && _waitingCommunicationCheckAck)
            {
                Thread.Sleep(100);
                i++;
            }
            return !_waitingCommunicationCheckAck;
        }

        private DateTime _acknowledgeFromMTRDateTime = DateTime.MinValue;
        public long GetMTRActiveTime(out DateTime acknowledgedTime)
        {
            var before = MTRResponseData.AcknowledgedTime_ms;
            Thread.Sleep(1000);
            SendData(@"Z");
            int waited = 0;
            while(MTRResponseData.AcknowledgedTime_ms == before && waited < 2000)
            {
                Thread.Sleep(100);
                waited += 100;
            }

            acknowledgedTime = _acknowledgeFromMTRDateTime;
            return MTRResponseData.AcknowledgedTime_ms;
        }

        private void KeepAliveTimerElapsed(Object stateInfo)
        {
            SendKeepAliveIfNecessary();
        }

        private void SendKeepAliveIfNecessary()
        {
            if(DateTime.Now.Ticks / DefaultValues.SystemTickDivider - _lastCommandSent > 10000 && SerialPortOpen())
            {
                SendData(@"Z");
            }
        }

        /// <summary>
        /// Wait max 200ms for the reply.
        /// And increase the communication errors count
        /// </summary>
        private void WaitForReply(string cmd)
        {
            var wait_count = 0;
            while(_waitAck)
            {
                if(wait_count++ >= 200 && cmd.Length >= 1)
                {
                    _waitAck = false;
                    IncreaseCommunicationErrorsCount();
                    break;
                }
                Thread.Sleep(1);
            }
        }

        private void ReportHardwareError()
        {
            if(HardwareErrorOccurred != null)
            {
                HardwareErrorOccurred(this, EventArgs.Empty);
            }
        }

        private void IncreaseCommunicationErrorsCount()
        {
            _communicationErrorsCount++;
            if(CommsErrorCountChanged != null)
            {
                CommsErrorCountChanged(this, EventArgs.Empty);
            }
            if(MTRCommunication != null)
            {
                var infoArgs = CreateInfoArgs(string.Format(@"Communication error count increased, count is now {0}", _communicationErrorsCount));
                MTRCommunication(this, infoArgs);
            }
        }

        private void ParseRxString(string rxString)
        {
            if(rxString.Length <= 1 && rxString != @"C")
            {
                IncreaseCommunicationErrorsCount();
                return;
            }

            var cmd = rxString[0];
            var data = rxString.Substring(1);
            if(MTRCommunication != null)
            {
                var eventArgs = new MTRCommandEventArgs { Command = cmd.ToString(), Data = data, Identifier = @"IN", DebugText = "debug" };
                MTRCommunication(this, eventArgs);
            }

            switch(cmd)
            {
                case 'N':
                case 'E':
                    HandleCommunicationError(cmd, data);
                    break;
                case 'U':
                    HandleKeyDown(data);
                    break;
                case 'D':
                    HandleDeviceDiagnostics(data);
                    break;
                case 'A':
                    HandleAck(data);
                    HandleAcknowledge(data);
                    break;
                case 'C':
                    HandleCalibration(data);
                    break;
                case 'L':
                    HandleRemoteControllerInfo(data);
                    break;
                default:
                    IncreaseCommunicationErrorsCount();
                    break;
            }
            SendKeepAliveIfNecessary();
        }

        private void HandleRemoteControllerInfo(string data)
        {
            if(string.IsNullOrEmpty(data) || data[0] != 'I') return;
            var splitted = data.Split(',');
            if(splitted.Length != 2)
            {
                IncreaseCommunicationErrorsCount();
                return;
            }
            MTRResponseData.RemoteControllerInfo = splitted[1];
        }

        private void HandleCalibration(string data)
        {
            if(string.IsNullOrEmpty(data)) return;
            try
            {
                MTRResponseData.PerimeterCalibrationFactors.SetValuesFromCommaSeparatedString(data);
            }
            catch
            {
                IncreaseCommunicationErrorsCount();
            }
        }

        private void HandleAcknowledge(string data)
        {
            if(!long.TryParse(data, NumberStyles.Integer, CultureInfo.InvariantCulture, out _lastAcknowledged_ms))
                return;

            //When R1 command sent, the powering up takes about 240ms so starting datetime is when its acknowledged with acktime 0 ms.
            if(_lastAcknowledged_ms == 0)
                _powerUpTime_utc = DateTime.UtcNow;

            _acknowledgeFromMTRDateTime = DateTime.UtcNow;
            MTRResponseData.AcknowledgedTime_ms = _lastAcknowledged_ms;
        }
        private void HandleAck(string data)
        {
            _waitAck = false;
            if(_waitingCommunicationCheckAck) _waitingCommunicationCheckAck = false;

            if(_stsArmed)
            {
                if(!long.TryParse(data, NumberStyles.Integer, CultureInfo.InvariantCulture, out _lastStsAcknowledgedMs))
                    return;

                if(_lastStsAcknowledgedMs > 0)
                {
                    MTRResponseData.STSAcknowledgedDelay_ms = _lastStsAcknowledgedMs - _lastKeyPressFromTestStart_ms; // For checking and reporting the time delay between user press and STS
                }
            }
        }


        private void HandleDeviceDiagnostics(string data)
        {
            MTRResponseData.DeviceDiagnostics = data;
        }

        private void HandleKeyDown(string data)
        {
            var dataArray = data.Split(',');
            if(dataArray.Length != 2 && dataArray.Length != 4)
            {
                IncreaseCommunicationErrorsCount();
                return;
            }
            if(dataArray.Length == 2 && (dataArray[0] != @"0" && dataArray[0] != @"1"))
            {
                IncreaseCommunicationErrorsCount();
                return;
            }
            if(dataArray.Length == 4 && (dataArray[0] != @"2" && dataArray[0] != @"3"))
            {
                IncreaseCommunicationErrorsCount();
                return;
            }
            int runningTimeIndex = dataArray.Length == 2 ? 1 : 3;
            int time_ms;
            if(!int.TryParse(dataArray[runningTimeIndex], NumberStyles.Integer, CultureInfo.InvariantCulture, out time_ms))
            {
                IncreaseCommunicationErrorsCount();
                return;
            }

            if(KeyPressed != null)
            {
                KeyPressed(this, EventArgs.Empty);
            }
        }

        private void HandleCommunicationError(char cmd, string data)
        {
            //CommunicationErrorParser.AddToCommunicationErrors(cmd, data, DateTime.UtcNow.Subtract(_powerUpTime_utc));
            IncreaseCommunicationErrorsCount();
        }

        private bool InitSerialPort()
        {
            // LOGException serialPort.IsOpen
            if(_serialPort != null && _serialPort.IsOpen && EnsureCommunicationToPerimeter())
                return true;

            // LOGException serialPort.IsOpen
            var serialPortNames = SerialPort.GetPortNames().ToArray();
            // TODO logging
            //Logger.AddMessageToLogQueue(@"Found serial ports: " + string.Join(@", ", serialPortNames));
            //Logger.FlushLogQueue();
            if(serialPortNames.Length < 1)
            {
                return false;
            }

            //try opening ports starting from highest port
            var success = false;
            for(var portIndex = serialPortNames.Length - 1; portIndex >= 0 && !success; portIndex--)
            {
                // LOGException new serialPort instance
                _serialPort = new SerialPort(serialPortNames[portIndex])
                {
                    BaudRate = ComSettings.BaudRate,
                    DataBits = ComSettings.DataBits,
                    Parity = ComSettings.Parity,
                    StopBits = ComSettings.StopBits,
                    DtrEnable = false,
                    RtsEnable = false,
                    ReadTimeout = 500,
                    WriteTimeout = 500,
                    NewLine = "\r"
                };
                try
                {
                    // LOGException serialPort.Open / IsOpen
                    _serialPort.Open();
                    if(_serialPort.IsOpen)
                    {
                        _serialPort.DataReceived += DataReceived;
                        _serialPort.ErrorReceived += SerialPortErrorReceived;
                        success = EnsureCommunicationToPerimeter();
                    }
                    if(!success && _serialPort.IsOpen)
                    {
                        // LOGException serialPort.Close
                        _serialPort.Close();
                    }
                }
                catch
                {
                    success = false;
                }
            }
            return success;
        }

        private void SerialPortErrorReceived(object sender, SerialErrorReceivedEventArgs e)
        {
            if(PerimeterCommunicationFailure != null)
            {
                PerimeterCommunicationFailure(this, EventArgs.Empty);
            }
        }

        private MTRCommandEventArgs CreateInfoArgs(string message)
        {
            return new MTRCommandEventArgs
            {
                Identifier = @"INFO",
                Command = string.Empty,
                Data = message,
                DebugText = "debug data"
            };
        }
    }
}

