#region Usings

using System;
using System.Collections.Generic;
using System.Linq;

#endregion

namespace NoSQL.GraphDB.Core.Helper
{
    /// <summary>
    /// Class for statistic calculations
    /// </summary>
    public static class Statistics
    {
        /// <summary>
        /// Calculates the average
        /// </summary>
        /// <param name="numbers">Double-values</param>
        /// <returns>Average</returns>
        public static double Average(List<double> numbers)
        {
            if (numbers != null) return numbers.Sum()/numbers.Count();

            throw new ArgumentException("Numbers are null or 0", "numbers");

        }

        /// <summary>
        /// Calculates the standard deviation
        /// </summary>
        /// <param name="numbers">Double-values</param>
        /// <returns>Standard deviation</returns>
        public static double StandardDeviation(List<double> numbers)
        {
            if (numbers != null)
            {
                var average = Average(numbers);

                return Math.Sqrt(numbers.Sum(_ => _ * _) / numbers.Count() - (average * average));
            }

            throw new ArgumentException("Numbers are null or 0", "numbers");
        }

        /// <summary>
        /// Calculates the median
        /// </summary>
        /// <param name="numbers">Double-values</param>
        /// <returns>Median</returns>
        public static double Median(List<double> numbers)
        {
            if (numbers != null && numbers.Count > 0)
            {
                if (numbers.Count() % 2 == 0)
                {
                    return numbers.OrderBy(_ => _).Skip(numbers.Count() / 2 - 1).Take(2).Sum() / 2;
                }
                return numbers.OrderBy(_ => _).ElementAt(Convert.ToInt32(Math.Floor((Convert.ToDouble(numbers.Count()) / 2))));
            }

            throw new ArgumentException("Numbers are null or 0", "numbers");
        }
    }
}
