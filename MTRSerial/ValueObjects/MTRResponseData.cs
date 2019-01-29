using System;
using System.Collections.Generic;
using System.Text;

namespace MTRSerial.ValueObjects
{
    static class MTRResponseData
    {
        public static long AcknowledgedTime_ms;
        public static long STSAcknowledgedDelay_ms;

        public static string RemoteControllerInfo { get; internal set; }
        public static string DeviceDiagnostics { get; internal set; }

        public static void Reset()
        {

        }

        public class PerimeterCalibrationFactors
        {
            public static void SetValuesFromCommaSeparatedString(string data)
            {
                throw new NotImplementedException();
            }
        }
    }
}
