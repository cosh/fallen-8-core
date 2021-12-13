using System;
using System.Collections.Generic;
using System.Text;

namespace NoSQL.GraphDB.Core.Helper
{
    public static class DateHelper
    {
        /// <summary>
        ///   The basic DateTime: 01.01.1970
        /// </summary>
        private static DateTime _nineTeenSeventy = new DateTime(1970, 1, 1, 0, 0, 0, 0);

        /// <summary>
        ///   Convertes the DateTime format to an Unix-TimeStamp
        /// </summary>
        /// <param name="date"> The DateTime </param>
        /// <returns> UInt32 representation </returns>
        public static UInt32 ConvertDateTime(DateTime date)
        {
            return (Convert.ToUInt32((date - _nineTeenSeventy).TotalSeconds));
        }

        /// <summary>
        ///   Returns the modification date as a delta from the creation date representation
        /// </summary>
        /// <param name="creationDate"> The creation date representation </param>
        /// <returns> The modification date delta </returns>
        public static UInt32 GetModificationDate(UInt32 creationDate)
        {
            return ConvertDateTime(DateTime.Now) - creationDate;
        }

        /// <summary>
        ///   Get a DateTime
        /// </summary>
        /// <param name="secondsFromNineTeenSeventy"> The seconds from 1970 </param>
        /// <returns> The DateTime </returns>
        public static DateTime GetDateTimeFromUnixTimeStamp(uint secondsFromNineTeenSeventy)
        {
            return _nineTeenSeventy.AddSeconds(secondsFromNineTeenSeventy);
        }
    }
}
