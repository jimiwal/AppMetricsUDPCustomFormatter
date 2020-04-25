
using App.Metrics;
using App.Metrics.Counter;
using App.Metrics.Formatters;
using App.Metrics.Formatters.Ascii;
using App.Metrics.Formatters.Ascii.Internal;
using App.Metrics.Reporting.Socket.Client;
using App.Metrics.Scheduling;
using App.Metrics.Serialization;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ConsoleApp1
{
    class Program
    {

        static async Task Main(string[] args)
        {
            var metrics = new MetricsBuilder()
                    .Report.OverUdp(
                    options =>
                    {
                        options.SocketSettings.Address = "localhost";
                        options.SocketSettings.Port = 11000;
                        options.MetricsOutputFormatter = new MyOutputFormatter()
                        {
                           
                        };
                        //options.Filter = filter;
                        //options.FlushInterval = TimeSpan.FromSeconds(config.Value.FlushIntervalInSeconds);
                        options.SocketPolicy = new SocketPolicy();
                    }
                )
                    .Build();

            Program program1 = new Program();
            program1.StartReporting += Program1_StartReporting1;

            program1.Start(metrics);

            var counter = new CounterOptions { Name = "my_counter" };
            metrics.Measure.Counter.Increment(counter);

            var counter2 = new CounterOptions { Name = "my_counter2" };
            metrics.Measure.Counter.Increment(counter2);

            Console.ReadKey();
        }

        private void Start(IMetricsRoot metricsRoot)
        {
            this.StartReporting?.Invoke(this, metricsRoot);
        }

        private async static void Program1_StartReporting1(object sender, IMetricsRoot metrics)
        {
            await Task.Run(() =>
            {
                var scheduler = new AppMetricsTaskScheduler(
                TimeSpan.FromSeconds(3),
                async () =>
                {
                    await Task.WhenAll(metrics.ReportRunner.RunAllAsync());
                });
                    scheduler.Start();
            });
        }

        public event EventHandler<IMetricsRoot> StartReporting;

  
    }

    internal class MyOutputFormatter : IMetricsOutputFormatter
    {
        private readonly MetricsTextOptions _options;
        /// <inheritdoc />
        public MetricsMediaTypeValue MediaType => new MetricsMediaTypeValue("text", "vnd.appmetrics.metrics", "v1", "plain");

        /// <inheritdoc />
        public MetricFields MetricFields { get; set; }

        /// <inheritdoc />
        public async Task WriteAsync(
            Stream output,
            MetricsDataValueSource metricsData,
            CancellationToken cancellationToken = default)
        {
            if (output == null)
            {
                throw new ArgumentNullException(nameof(output));
            }

            var serializer = new MetricSnapshotSerializer();

            using var streamWriter = new StreamWriter(output, System.Text.Encoding.UTF8, bufferSize: 1024, leaveOpen: true);

            using var textWriter = new MetricSnapshotTextWriter2(
                streamWriter,
                " ",
                10,
                (metricContext, metricName) => {
                    return string.IsNullOrWhiteSpace(metricContext)
                    ? metricName
                    : $"[{metricContext}] {metricName}";
                });

            serializer.Serialize(textWriter, metricsData, MetricFields);
            await Task.CompletedTask; 
        }
    }

    public class MetricSnapshotTextWriter2 : IMetricSnapshotWriter
    {
        private readonly string _separator;
        private readonly int _padding;
        private readonly TextWriter _textWriter;
        private readonly Func<string, string, string> _metricNameFormatter;
        private readonly MetricsTextPoints _textPoints;

        public MetricSnapshotTextWriter2(
            TextWriter textWriter,
            string separator = MetricsTextFormatterConstants.OutputFormatting.Separator,
            int padding = MetricsTextFormatterConstants.OutputFormatting.Padding,
            Func<string, string, string> metricNameFormatter = null)
        {
            _textWriter = textWriter ?? throw new ArgumentNullException(nameof(textWriter));
            _separator = separator;
            _padding = padding;
            _textPoints = new MetricsTextPoints();
            if (metricNameFormatter == null)
            {
                _metricNameFormatter = (metricContext, metricName) => string.IsNullOrWhiteSpace(metricContext)
                    ? metricName
                    : $"[{metricContext}] {metricName}";
            }
            else
            {
                _metricNameFormatter = metricNameFormatter;
            }
        }

        /// <inheritdoc />
        public void Write(
            string context,
            string name,
            string field,
            object value,
            MetricTags tags,
            DateTime timestamp)
        {
            if (value == null)
            {
                return;
            }

            var measurement = _metricNameFormatter(context, name);

            _textPoints.Add(new MetricsTextPoint(measurement, new Dictionary<string, object> { { field, value } }, tags, timestamp));
        }

        /// <inheritdoc />
        public void Write(
            string context,
            string name,
            IEnumerable<string> columns,
            IEnumerable<object> values,
            MetricTags tags,
            DateTime timestamp)
        {
            var fields = columns.Zip(values, (column, data) => new { column, data }).ToDictionary(pair => pair.column, pair => pair.data);

            if (!fields.Any())
            {
                return;
            }

            var measurement = _metricNameFormatter(context, name);

            _textPoints.Add(new MetricsTextPoint(measurement, fields, tags, timestamp));
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            return DisposeAsync(true);
        }

        /// <summary>
        /// Releases unmanaged and - optionally - managed resources.
        /// </summary>
        /// <param name="disposing"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
        protected virtual async ValueTask DisposeAsync(bool disposing)
        {
            if (disposing)
            {
                await _textPoints.WriteAsync(_textWriter, _separator, _padding);
                _textWriter?.Dispose();
            }
        }

        public void Dispose()
        {
            DisposeAsync();
        }
    }


    internal class MetricsTextPoint
    {
        private readonly DateTime _timestamp;

        public MetricsTextPoint(
            string measurement,
            IReadOnlyDictionary<string, object> fields,
            MetricTags tags,
            DateTime timestamp)
        {
            if (string.IsNullOrEmpty(measurement))
            {
                throw new ArgumentException("A measurement name must be specified");
            }

            if (fields == null || fields.Count == 0)
            {
                throw new ArgumentException("At least one field must be specified");
            }

            if (fields.Any(f => string.IsNullOrEmpty(f.Key)))
            {
                throw new ArgumentException("Fields must have non-empty names");
            }

            _timestamp = timestamp;

            Measurement = measurement;
            Fields = fields;
            Tags = tags;
        }

        private IReadOnlyDictionary<string, object> Fields { get; }

        private string Measurement { get; }

        private MetricTags Tags { get; }

        public async ValueTask WriteAsync(TextWriter textWriter, string separator, int padding)
        {
            //aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
            if (textWriter == null)
            {
                throw new ArgumentNullException(nameof(textWriter));
            }

            await textWriter.WriteAsync(Measurement); await textWriter.WriteAsync(' ');
            
            await textWriter.WriteAsync(Fields.Select(k => k.Value).FirstOrDefault().ToString()); await textWriter.WriteAsync(' ');
            await textWriter.WriteAsync(Tags.Values.FirstOrDefault().ToString());

            await textWriter.WriteAsync(_timestamp.Ticks.ToString()); await textWriter.WriteAsync(' ');

            await textWriter.WriteAsync('\n');

        }
    }

    internal class MetricsTextPoints
    {
        private readonly List<MetricsTextPoint> _points = new List<MetricsTextPoint>();

        public void Add(MetricsTextPoint textPoint)
        {
            if (textPoint == null)
            {
                return;
            }

            _points.Add(textPoint);
        }

        public async ValueTask WriteAsync(TextWriter textWriter, string separator, int padding)
        {
            if (textWriter == null)
            {
                return;
            }

            var points = _points.ToList();

            foreach (var point in points)
            {
                await point.WriteAsync(textWriter, separator, padding);
                await textWriter.WriteAsync('\n');
            }
        }
    }


    internal static class MetricsTextSyntax
    {
        private static readonly Dictionary<Type, Func<object, string>> Formatters = new Dictionary<Type, Func<object, string>>
                                                                                    {
                                                                                        { typeof(sbyte), FormatInteger },
                                                                                        { typeof(byte), FormatInteger },
                                                                                        { typeof(short), FormatInteger },
                                                                                        { typeof(ushort), FormatInteger },
                                                                                        { typeof(int), FormatInteger },
                                                                                        { typeof(uint), FormatInteger },
                                                                                        { typeof(long), FormatInteger },
                                                                                        { typeof(ulong), FormatInteger },
                                                                                        { typeof(float), FormatFloat },
                                                                                        { typeof(double), FormatFloat },
                                                                                        { typeof(decimal), FormatFloat },
                                                                                        { typeof(bool), FormatBoolean },
                                                                                        { typeof(TimeSpan), FormatTimespan }
                                                                                    };

        public static string FormatReadable(string label, string value, string separator, int padding)
        {
            var pad = string.Empty;

            if (label.Length + 2 + separator.Length < padding)
            {
                pad = new string(' ', padding - label.Length - 1 - separator.Length);
            }

            return $"{pad}{label} {separator} {value}";
        }

        public static string FormatValue(object value)
        {
            var v = value ?? string.Empty;

            return Formatters.TryGetValue(v.GetType(), out Func<object, string> format)
                ? format(v)
                : v.ToString();
        }

        private static string FormatBoolean(object b) { return (bool)b ? "true" : "false"; }

        private static string FormatFloat(object f) { return ((IFormattable)f).ToString(null, CultureInfo.InvariantCulture); }

        private static string FormatInteger(object i) { return ((IFormattable)i).ToString(null, CultureInfo.InvariantCulture); }

        private static string FormatTimespan(object ts) { return ((TimeSpan)ts).TotalMilliseconds.ToString(CultureInfo.InvariantCulture) + "ms"; }
    }
}
