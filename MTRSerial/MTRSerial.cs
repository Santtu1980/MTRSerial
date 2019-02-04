using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml.Serialization;
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


        private volatile int _communicationErrorsCount;               // Counter for communication errors
        private volatile bool _waitAck;
        private DateTime _powerUpTime_utc;
        private readonly object _commLock = new object();
        private long _lastStsAcknowledgedMs;
        private SerialPort _serialPort;
        private volatile bool _waitingCommunicationCheckAck;
        private long _lastCommandSent;

        public MTRSerialPort()
        {
            _serialPort = new SerialPort();
        }

        /// <summary>
        /// Returns the serial communication starting time (UTC)
        /// </summary>
        public DateTime GetPowerUpTime_utc => _powerUpTime_utc.ToUniversalTime();

        /// <summary>
        /// Send command to the MTR
        /// </summary>
        /// <param name="command">Command to send</param>
        /// <param name="binary">Some commands needs extra binarydata. If it is needed and no binary data given an exception will throw</param>
        public void AskFromMTR(CommandsToMTR.CommandName command, byte[] binary = null)
        {
            var cmd = GetCommand(command, binary);
            SendData(cmd);
        }

        /// <summary>
        /// In some cases to "reboot" connection it need to close and reopen
        /// </summary>
        /// <returns></returns>
        public bool CloseAndReopenSerialPort()
        {
            CloseSerialPort();
            Thread.Sleep(500);// The best practice for any application is to wait for some amount of time after calling the Close method before attempting to call the Open method, as the port may not be closed instantly. From:https://msdn.microsoft.com/en-us/library/system.io.ports.serialport.close.aspx
            return OpenSerialPort();
        }

        /// <summary>
        /// Closes the Serial port and stop communication
        /// </summary>
        public void CloseSerialPort()
        {
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

            SerialPortClosed?.Invoke(this, EventArgs.Empty);
        }
        
        /// <summary>
        /// Opens the Serial port and enables the communication
        /// </summary>
        /// <returns></returns>
        public bool OpenSerialPort()
        {
            // Init the serial port and communications log
            var success = InitSerialPort();
            if(_serialPort != null && success)
            {
                ResetCommunicationErrorsCount();

                SerialPortOpened?.Invoke(this, EventArgs.Empty);

                if(MTRCommunication != null)
                {
                    var infoArgs = CreateInfoArgs(@"Serial port opened");
                    MTRCommunication(this, infoArgs);
                }
            }
            return success;
        }

        /// <summary>
        /// The amount of communication errors increase if there occurs some errors. This method returns the amount of the errors. It can be reset by using "ResetCommunicationErrorsCount"
        /// </summary>
        /// <returns></returns>
        public int GetAmountOfCommunicationErrors()
        {
            return _communicationErrorsCount;
        }
        
        /// <summary>
        /// This resets the number of the communication errors
        /// </summary> 
        private void ResetCommunicationErrorsCount()
        {
            _communicationErrorsCount = 0;
            CommsErrorCountChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// This returns true if the the Serial port connection is open
        /// </summary>
        /// <returns>True if open, otherwise false</returns>
        public bool IsSerialPortOpen()
        {
            return _serialPort != null && _serialPort.IsOpen;
        }

        /// <summary>
        /// Event handler when some data received from the serial port
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
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
                while(threw == false)
                {
                    try
                    {
                        buffer.Add(_serialPort.ReadByte());
                    }
                    catch (Exception ex)
                    {
                        threw = true;
                    }
                }

                var temp = new BitArray(new int[]{buffer[0],buffer[1]});

                WriteValuesToFile(buffer);
                ParseRxString(buffer);
            }
        }
        public List<int> buffer = new List<int>();

        public void ClearBuffer()
        {
            buffer = new List<int>();
        }
        /// <summary>
        /// Just asking a status to check if connection is ok
        /// </summary>
        /// <returns><c>False</c> if  there isn't connection after 20 tries.<c>True</c> if there came some message</returns>
        public bool EnsureCommunicationToMTR()
        {
            _waitingCommunicationCheckAck = true;
            SendData(@"/ST"); 
            var triesToWaitAck = 0;
            while(triesToWaitAck < 20 && _waitingCommunicationCheckAck)
            {
                Thread.Sleep(100);
                triesToWaitAck++;
            }
            return !_waitingCommunicationCheckAck;
        }

        private void SendKeepAliveIfNecessary()
        {
            if(DateTime.Now.Ticks / DefaultValues.SystemTickDivider - _lastCommandSent > 10000 && IsSerialPortOpen())
            {
                SendData(@"/ST");
            }
        }

        private string GetCommand(CommandsToMTR.CommandName command, byte[] binary = null)
        {
            if(command == CommandsToMTR.CommandName.SpoolBinary ||
               command == CommandsToMTR.CommandName.GetMessageBinary ||
               command == CommandsToMTR.CommandName.SetClock
                && binary == null)
            {
                throw new Exception("Binary content missing.");
            }

            switch(command)
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

        private void SendData(string cmd)
        {
            if(string.IsNullOrEmpty(cmd)) return;
            if(cmd.Contains(@"/NS"))
            {
                _powerUpTime_utc = DateTime.UtcNow;
            }

            WaitForReply(cmd);

            _waitAck = true;

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
        
        private void WaitForReply(string cmd)
        {
            var waitCount = 0;
            while(_waitAck)
            {
                if(waitCount++ >= 200 && cmd.Length >= 1)
                {
                    _waitAck = false;
                    IncreaseCommunicationErrorsCount();
                    break;
                }
                Thread.Sleep(1);
            }
        }
        
        private void IncreaseCommunicationErrorsCount()
        {
            _communicationErrorsCount++;
            CommsErrorCountChanged?.Invoke(this, EventArgs.Empty);

            if(MTRCommunication != null)
            {
                var infoArgs = CreateInfoArgs(string.Format(@"Communication error count increased, count is now {0}", _communicationErrorsCount));
                MTRCommunication(this, infoArgs);
            }
        }

        private void ParseRxString(List<int> rxByteList)
        {
            if(rxByteList.Count <= 1) // && rxString != @"C")
            {
                IncreaseCommunicationErrorsCount();
                return;
            }

            var startByte1 = rxByteList[0];
            var startByte2 = rxByteList[1];
            var emit1 = rxByteList[2];
            var emit2 = rxByteList[3];
            var emit3 = rxByteList[4];
            var emit4 = rxByteList[5];
            var notInUse1 = rxByteList[6];
            var productionWeek = rxByteList[7];
            var productionYear = rxByteList[8];
            var notInUse2 = rxByteList[9];
            var cardCheckByte = rxByteList[10];
            

            if (startByte1.Equals(32) &&
                startByte2.Equals(32))
            {
                List<MTRResponseCheckPoint> checkPoints = new List<MTRResponseCheckPoint>();

                for(int checkPointNo = 0; checkPointNo < 50; checkPointNo++)
                {
                    var checkPointDataPosition = 3 * checkPointNo;
                    var checkPoint = new MTRResponseCheckPoint();
                    var codeN = rxByteList[checkPointDataPosition];
                    var timeN = rxByteList[checkPointDataPosition+1] << 8 | rxByteList[checkPointDataPosition + 2];

                    checkPoints.Add(new MTRResponseCheckPoint{CodeN = codeN, TimeN_s = timeN});
                }
                //byte[] name = {rxByteList[160], rxByteList[161], rxByteList[162], rxByteList[163], rxByteList[164], rxByteList[165], rxByteList[166], rxByteList[167], rxByteList[168] }
                //    .Concat(bytes2).ToArray()};
            }

            var cmd = rxByteList[6];
            var data = string.Join(",", rxByteList);
            //if(MTRCommunication != null)
            //{
            //    var eventArgs = new MTRCommandEventArgs { Command = cmd.ToString(), Data = data, Identifier = @"IN", DebugText = "debug" };
            //    MTRCommunication(this, eventArgs);
            //}

            switch(cmd)
            {
                case 'M':
                    HandleMTRDataMessage(data);
                    break;
                case 'S':
                    HandleMTRStatusMessage(data);
                    break;
                default:
                    IncreaseCommunicationErrorsCount();
                    break;
            }
            SendKeepAliveIfNecessary();
        }

        private void HandleAck(string data)
        {
            _waitAck = false;
            if(_waitingCommunicationCheckAck) _waitingCommunicationCheckAck = false;
            
            {
                if(!long.TryParse(data, NumberStyles.Integer, CultureInfo.InvariantCulture, out _lastStsAcknowledgedMs))
                    return;

                if(_lastStsAcknowledgedMs > 0)
                {
                    //MTRResponseData.STSAcknowledgedDelay_ms = _lastStsAcknowledgedMs - _lastKeyPressFromTestStart_ms; // For checking and reporting the time delay between user press and STS
                }
            }
        }


        private void HandleDeviceDiagnostics(string data)
        {
           // MTRResponseData.DeviceDiagnostics = data;
        }

        private void HandleMTRDataMessage(string data)
        {
            //MTR--datamessage
            //    ----------------
            //Fieldname     # bytes
            //Preamble      4 FFFFFFFF(hex)(4 "FF"'s never occur "inside" a message).
            //Package -size 1 number of bytes excluding preamble(= 230)
            //Package -type 1 'M' as "MTR-datamessage".
            //MTR - id      2 Serial number of MTR2; Least significant byte first
            //Timestamp     6 Binary Year, Month, Day, Hour, Minute, Second
            //TS - [ms]     2 Milliseconds NOT YET USED, WILL BE 0 IN THIS VERSION
            //Package#      4 Binary Counter, from 1 and up; Least sign byte first
            //Card - id     3 Binary, Least sign byte first
            //Producweek 1  0 - 53; 0 when package is retrived from "history"
            //Producyear 1  94 - 99,0 -..X; 0 when package is retrived from "history"
            //ECardHeadSum  1 Headchecksum from card; 0 when package is retrived from "history"

            //The following fields are repeated 50 times:
            //CodeN         1 ControlCode; unused positions have 0
            //TimeN         2 Time binary seconds.Least sign. first, Most sign. last; unused: 0
            //ASCII string  56 Various info depending on ECard - type; 20h when retr.from "history"(See ASCIIstring)
            //Checksum      1 Binary SUM(MOD 256) of all bytes including Preamble
            //NULL - Filler 1 Binary 0(to avoid potential 5 FF's. Making it easier to haunt PREAMBLE
            //    ----------------------------------------
            //Size 234


            var MTRDataStrings = new MTRDataString();
            MTRDataStrings.Preamble = data.Substring(0, 4); // Ensure the message start with preamble
            MTRDataStrings.PackageSize = data.Substring(4); // Packet size, should be 230 when datamessage
            MTRDataStrings.PackageType = data.Substring(5); // Packet type, should be M as MTR message
            MTRDataStrings.MtrSerialNo = data.Substring(6, 2); //serialNumber of the MTR reader
            MTRDataStrings.TimeStamp = data.Substring(8, 6); // binary year, month day, hour, minute, second
            MTRDataStrings.Time_ms = data.Substring(14, 2); // Time is not yet in use
            MTRDataStrings.PackageNo = data.Substring(15, 4); // counter from 1 to up
            MTRDataStrings.CardId = data.Substring(19, 3); // binary, least sign first
            // Next tree is used when data retrived from history
            //MTRData.ProductWeek = data.Substring(22, 53);
            //MTRData.ProductYear = data.Substring(94,5);
            //MTRData.ECardHeadSum = data.Substring(100);

            List<MTRResponseCheckPoint> checkPoints = new List<MTRResponseCheckPoint>();

            for (int checkPointNo = 0; checkPointNo < 50; checkPointNo++)
            {
                var checkPointDataPosition = 61 * checkPointNo;
                var checkPoint = new MTRResponseCheckPoint();
                var codeN = data.Substring(100 + checkPointDataPosition, 1);
                var timeN = data.Substring(102 + checkPointDataPosition, 2);
                var info = data.Substring(103 + checkPointDataPosition, 56);
                var checkSum = data.Substring(159 + checkPointDataPosition, 1);
                var filler = data.Substring(160 + checkPointDataPosition, 1);

                MTRDataStrings.CheckPoints.Add(new []{codeN, timeN, info, checkSum, filler});
            }

            var mtrData = ConvertDataStringToTypeData(MTRDataStrings);
            //var listDatas = new List<MTRData>();
            //listDatas.Add(mtrData);
            //WriteValuesToFile(listDatas);
        }

        private void HandleMTRStatusMessage(string data)
        {
            //MTR--datamessage
            //    ----------------
            //Fieldname     # bytes
            //Preamble      4 FFFFFFFF(hex)(4 "FF"'s never occur "inside" a message).
            //Package -size 1 number of bytes excluding preamble(= 230)
            //Package -type 1 'M' as "MTR-datamessage".
            //MTR - id      2 Serial number of MTR2; Least significant byte first
            //Timestamp     6 Binary Year, Month, Day, Hour, Minute, Second
            //TS - [ms]     2 Milliseconds NOT YET USED, WILL BE 0 IN THIS VERSION
            //Package#      4 Binary Counter, from 1 and up; Least sign byte first
            //Card - id     3 Binary, Least sign byte first
            //Producweek 1  0 - 53; 0 when package is retrived from "history"
            //Producyear 1  94 - 99,0 -..X; 0 when package is retrived from "history"
            //ECardHeadSum  1 Headchecksum from card; 0 when package is retrived from "history"

            //The following fields are repeated 50 times:
            //CodeN         1 ControlCode; unused positions have 0
            //TimeN         2 Time binary seconds.Least sign. first, Most sign. last; unused: 0
            //ASCII string  56 Various info depending on ECard - type; 20h when retr.from "history"(See ASCIIstring)
            //Checksum      1 Binary SUM(MOD 256) of all bytes including Preamble
            //NULL - Filler 1 Binary 0(to avoid potential 5 FF's. Making it easier to haunt PREAMBLE
            //    ----------------------------------------
            //Size 234


            var MTRDataStrings = new MTRDataString();
            MTRDataStrings.Preamble = data.Substring(0, 4); // Ensure the message start with preamble
            MTRDataStrings.PackageSize = data.Substring(4); // Packet size, should be 230 when datamessage
            MTRDataStrings.PackageType = data.Substring(5); // Packet type, should be M as MTR message
            MTRDataStrings.MtrSerialNo = data.Substring(6, 2); //serialNumber of the MTR reader
            MTRDataStrings.TimeStamp = data.Substring(8, 6); // binary year, month day, hour, minute, second
            MTRDataStrings.Time_ms = data.Substring(14, 2); // Time is not yet in use
            MTRDataStrings.PackageNo = data.Substring(15, 4); // counter from 1 to up
            MTRDataStrings.CardId = data.Substring(19, 3); // binary, least sign first
            // Next tree is used when data retrived from history
            //MTRData.ProductWeek = data.Substring(22, 53);
            //MTRData.ProductYear = data.Substring(94,5);
            //MTRData.ECardHeadSum = data.Substring(100);

            List<MTRResponseCheckPoint> checkPoints = new List<MTRResponseCheckPoint>();

            for(int checkPointNo = 0; checkPointNo < 50; checkPointNo++)
            {
                var checkPointDataPosition = 61 * checkPointNo;
                var checkPoint = new MTRResponseCheckPoint();
                var codeN = data.Substring(100 + checkPointDataPosition, 1);
                var timeN = data.Substring(102 + checkPointDataPosition, 2);
                var info = data.Substring(103 + checkPointDataPosition, 56);
                var checkSum = data.Substring(159 + checkPointDataPosition, 1);
                var filler = data.Substring(160 + checkPointDataPosition, 1);

                MTRDataStrings.CheckPoints.Add(new[] { codeN, timeN, info, checkSum, filler });
            }

            var mtrData = ConvertDataStringToTypeData(MTRDataStrings);
            var listDatas = new List<MTRData>();
            listDatas.Add(mtrData);
            //WriteValuesToFile(listDatas);
        }

        private void WriteValuesToFile(List<int> data)
        {
            try
            {
                var fileName = @"C:\Temp\mtr.xml";
                var serializerObj4 = new XmlSerializer(typeof(List<int>));
                TextWriter writeFileStream4 = new StreamWriter(fileName, false);
                serializerObj4.Serialize(writeFileStream4, data);
                writeFileStream4.Close();
            }
            catch(Exception ex)
            {
                throw ex;
            }
        }

        private MTRData ConvertDataStringToTypeData(MTRDataString dataString)
        {
            var mtrData = new MTRData();
            try
            {
                mtrData.Preamble = int.Parse(dataString.Preamble); // Ensure the message start with preamble
                mtrData.PackageSize = int.Parse(dataString.PackageSize); // Packet size, should be 230 when datamessage
                mtrData.PackageType = dataString.PackageType[0]; // Packet type, should be M as MTR message
                mtrData.MtrSerialNo = int.Parse(dataString.MtrSerialNo); //serialNumber of the MTR reader
                mtrData.TimeStamp = DateTime.Parse(dataString.TimeStamp); // binary year, month day, hour, minute, second
                mtrData.Time_ms = long.Parse(dataString.Time_ms); // Time is not yet in use
                mtrData.PackageNo = int.Parse(dataString.PackageNo); // counter from 1 to up
                mtrData.CardId = int.Parse(dataString.CardId); // binary, least sign first
                // Next tree is used when data retrived from history
                //mtrData.ProductWeek = dataString.ProductWeek;
                //mtrData.ProductYear = dataString.ProductYear;
                //mtrData.ECardHeadSum = dataString.ECardHeadSum;

                foreach (string[] checkPonit_s in dataString.CheckPoints)
                {
                    var checkPoint = new MTRResponseCheckPoint();
                    checkPoint.CodeN = int.Parse(checkPonit_s[0]);
                    checkPoint.TimeN_s = int.Parse(checkPonit_s[1]);
                    checkPoint.InfoField = checkPonit_s[2];
                    checkPoint.CheckSum = int.Parse(checkPonit_s[3]);
                    checkPoint.FillerNull = int.Parse(checkPonit_s[4]);
                    mtrData.CheckPoints.Add(checkPoint);
                }
            }
            catch (Exception ex)
            {
                return null;
            }

            return mtrData;
        }


        private bool InitSerialPort()
        {
            // LOGException serialPort.IsOpen
            if(_serialPort != null && _serialPort.IsOpen && EnsureCommunicationToMTR())
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
                    //DtrEnable = false,
                    //RtsEnable = false,
                    //ReadTimeout = 500,
                    //WriteTimeout = 500,
                    //NewLine = "\r"
                };
                try
                {
                    // LOGException serialPort.Open / IsOpen
                    _serialPort.DtrEnable = true;
                    _serialPort.Open();
                    if(_serialPort.IsOpen)
                    {
                        _serialPort.DataReceived += DataReceived;
                        _serialPort.ErrorReceived += SerialPortErrorReceived;
                        //success = EnsureCommunicationToMTR();
                    }
                    //if(!success && _serialPort.IsOpen)
                    //{
                    //    // LOGException serialPort.Close
                    //    _serialPort.Close();
                    //}
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

