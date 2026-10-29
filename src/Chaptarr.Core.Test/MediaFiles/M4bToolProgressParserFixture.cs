using NUnit.Framework;
using NzbDrone.Core.MediaFiles;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class M4bToolProgressParserFixture
    {
        [Test]
        public void should_parse_percent_from_carriage_return_progress_bar()
        {
            var parser = new M4bToolProgressParser();

            var parsed = parser.TryParse(" 12/70 [=====>----------------------] 17%\r", out var update);

            Assert.That(parsed, Is.True);
            Assert.That(update.Progress, Is.EqualTo(16.29m).Within(0.01m));
            Assert.That(update.Message, Is.EqualTo("Converting to M4B - 12 of 70"));
        }

        [Test]
        public void should_parse_current_total_when_percent_is_missing()
        {
            var parser = new M4bToolProgressParser();

            var parsed = parser.TryParse("Processing 5/20\r", out var update);

            Assert.That(parsed, Is.True);
            Assert.That(update.Progress, Is.EqualTo(23.75m));
            Assert.That(update.Message, Is.EqualTo("Converting to M4B - 5 of 20"));
        }

        [Test]
        public void should_parse_decimal_percent_using_invariant_format()
        {
            var parser = new M4bToolProgressParser();

            var parsed = parser.TryParse(" 1/8 [===>------------------------] 12.5%\r", out var update);

            Assert.That(parsed, Is.True);
            Assert.That(update.Progress, Is.EqualTo(11.875m));
            Assert.That(update.Message, Is.EqualTo("Converting to M4B - 1 of 8"));
        }

        [Test]
        public void should_parse_progress_split_across_chunks()
        {
            var parser = new M4bToolProgressParser();

            Assert.That(parser.TryParse(" 12/", out _), Is.False);
            var parsed = parser.TryParse("70 [=====>----------------------] 17%\r", out var update);

            Assert.That(parsed, Is.True);
            Assert.That(update.Progress, Is.EqualTo(16.29m).Within(0.01m));
        }

        [Test]
        public void should_not_treat_per_file_percent_as_whole_job_percent()
        {
            var parser = new M4bToolProgressParser(70);

            Assert.That(parser.TryParse(" 1/70 [>---------------------------] 1%\r", out var firstUpdate), Is.True);
            Assert.That(firstUpdate.Progress, Is.EqualTo(1.36m).Within(0.01m));

            var parsed = parser.TryParse("frame=100 time=00:03:00.00 speed=40x 100%\r", out var update);

            Assert.That(parsed, Is.True);
            Assert.That(update.Progress, Is.EqualTo(1.36m).Within(0.01m));
            Assert.That(update.Message, Is.EqualTo("Converting to M4B - 1 of 70"));
        }

        [Test]
        public void should_keep_progress_monotonic_when_tool_reports_next_file_percent_reset()
        {
            var parser = new M4bToolProgressParser(70);

            Assert.That(parser.TryParse(" 10/70 [====>-----------------------] 14%\r", out var firstUpdate), Is.True);
            Assert.That(firstUpdate.Progress, Is.EqualTo(13.57m).Within(0.01m));

            var parsed = parser.TryParse("frame=1 time=00:00:01.00 speed=40x 0%\r", out var update);

            Assert.That(parsed, Is.True);
            Assert.That(update.Progress, Is.EqualTo(13.57m).Within(0.01m));
        }

        [Test]
        public void should_reserve_final_five_percent_for_chaptarr_verification()
        {
            var parser = new M4bToolProgressParser(70);

            var parsed = parser.TryParse("Processing 70/70\r", out var update);

            Assert.That(parsed, Is.True);
            Assert.That(update.Progress, Is.EqualTo(95m));
            Assert.That(update.Message, Is.EqualTo("Converting to M4B - 70 of 70"));
        }

        [Test]
        public void should_parse_processing_file_of_total_format()
        {
            var parser = new M4bToolProgressParser(17);

            var parsed = parser.TryParse("Processing file 8 of 17\r", out var update);

            Assert.That(parsed, Is.True);
            Assert.That(update.Progress, Is.EqualTo(44.70m).Within(0.01m));
            Assert.That(update.Message, Is.EqualTo("Converting to M4B - 8 of 17"));
            Assert.That(update.CurrentFile, Is.EqualTo(8));
            Assert.That(update.TotalFiles, Is.EqualTo(17));
        }

        [Test]
        public void should_parse_m4b_tool_verbose_remaining_total_format()
        {
            var parser = new M4bToolProgressParser(8);

            var parsed = parser.TryParse("\r   6 remaining /    8 total /", out var update);

            Assert.That(parsed, Is.True);
            Assert.That(update.Progress, Is.EqualTo(23.75m));
            Assert.That(update.Message, Is.EqualTo("Converting to M4B - 2 of 8"));
            Assert.That(update.CurrentFile, Is.EqualTo(2));
            Assert.That(update.TotalFiles, Is.EqualTo(8));
        }

        [Test]
        public void should_treat_zero_remaining_as_conversion_phase_complete()
        {
            var parser = new M4bToolProgressParser(8);

            var parsed = parser.TryParse("\r   0 remaining /    8 total, preparing next task -", out var update);

            Assert.That(parsed, Is.True);
            Assert.That(update.Progress, Is.EqualTo(95m));
            Assert.That(update.Message, Is.EqualTo("Finalizing M4B"));
            Assert.That(update.CurrentFile, Is.EqualTo(8));
            Assert.That(update.TotalFiles, Is.EqualTo(8));
        }

        [Test]
        public void should_prefer_newer_remaining_status_over_older_step_status()
        {
            var parser = new M4bToolProgressParser(8);

            Assert.That(parser.TryParse("Processing file 8 of 8\r", out _), Is.True);
            var parsed = parser.TryParse("\r   0 remaining /    8 total, preparing next task -", out var update);

            Assert.That(parsed, Is.True);
            Assert.That(update.Progress, Is.EqualTo(95m));
            Assert.That(update.Message, Is.EqualTo("Finalizing M4B"));
        }

        [Test]
        public void should_ignore_invalid_current_total_pairs()
        {
            var parser = new M4bToolProgressParser();

            var parsed = parser.TryParse("not progress 2026/04/25\r", out _);

            Assert.That(parsed, Is.False);
        }

        [Test]
        public void should_drop_the_step_counter_for_a_single_file_conversion()
        {
            var parser = new M4bToolProgressParser(1);

            var parsed = parser.TryParse("1 remaining / 1 total\r", out var update);

            // One file is one m4b-tool task, so this counter reads "0 of 1" for the whole
            // run - a 23 minute transcode that looks identical to a hung process.
            Assert.That(parsed, Is.True);
            Assert.That(update.Message, Is.EqualTo("Converting to M4B"));
        }

        [Test]
        public void should_still_finalize_a_single_file_conversion()
        {
            var parser = new M4bToolProgressParser(1);

            var parsed = parser.TryParse("0 remaining / 1 total\r", out var update);

            Assert.That(parsed, Is.True);
            Assert.That(update.Message, Is.EqualTo("Finalizing M4B"));
        }

        [TestCase(0L, "Converting to M4B - 0 MB written")]
        [TestCase(299892780L, "Converting to M4B - 286 MB written")]
        [TestCase(1073741824L, "Converting to M4B - 1.0 GB written")]
        [TestCase(6442450944L, "Converting to M4B - 6.0 GB written")]
        public void should_format_bytes_written_heartbeat(long bytes, string expected)
        {
            Assert.That(M4bToolProgressParser.FormatBytesWrittenMessage(bytes), Is.EqualTo(expected));
        }
    }
}
