using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Books;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Queue;

namespace Chaptarr.Core.Test.Queue
{
    [TestFixture]
    public class UntrackedConversionQueueFixture
    {
        private sealed class NoOpEventAggregator : IEventAggregator
        {
            public void PublishEvent<TEvent>(TEvent @event)
                where TEvent : class, IEvent
            {
            }
        }

        private class ThrowingProxy<T> : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                throw new NotImplementedException($"Test proxy does not implement {typeof(T).Name}.{targetMethod?.Name}");
            }
        }

        private static (QueueService Queue, ConversionTrackingService Tracking, string DownloadId) CreateSubject()
        {
            var tracking = new ConversionTrackingService(new NoOpEventAggregator());
            var queue = new QueueService(
                new NoOpEventAggregator(),
                DispatchProxy.Create<IHistoryService, ThrowingProxy<IHistoryService>>(),
                tracking);

            return (queue, tracking, $"local-{Guid.NewGuid():N}");
        }

        private static NzbDrone.Core.Queue.Queue FindRow(QueueService queue, string downloadId)
        {
            return queue.GetQueue().SingleOrDefault(q => q.DownloadId == downloadId);
        }

        [Test]
        public void should_surface_an_untracked_conversion_as_a_queue_row()
        {
            var (queue, tracking, downloadId) = CreateSubject();
            var author = new Author { Id = 11, Name = "David Archer" };
            var book = new Book { Id = 22, Title = "Black Sheep", Author = author, AuthorId = author.Id };

            using var cancellation = new CancellationTokenSource();

            try
            {
                tracking.RegisterUntracked(downloadId, new UntrackedConversionContext
                {
                    Author = author,
                    Book = book,
                    Title = book.Title,
                    Quality = new QualityModel(Quality.MP3),
                    Size = 1024,
                    Added = DateTime.UtcNow
                });

                tracking.Start(downloadId, Quality.M4B.Id, "M4B", "Converting to M4B");
                tracking.RegisterCancellation(downloadId, cancellation);
                tracking.Progress(downloadId, 42m, "Converting to M4B - 1 of 2");

                var row = FindRow(queue, downloadId);

                Assert.That(row, Is.Not.Null);
                Assert.That(row.Id, Is.Not.Zero);
                Assert.That(row.Author, Is.SameAs(author));
                Assert.That(row.Book, Is.SameAs(book));
                Assert.That(row.Title, Is.EqualTo("Black Sheep"));

                // The queue's status cell nests every conversion state under "completed", so
                // this is what makes an untracked conversion render like a tracked one.
                Assert.That(row.Status, Is.EqualTo("completed"));
                Assert.That(row.ConversionStatus, Is.EqualTo("converting"));
                Assert.That(row.ConversionProgress, Is.EqualTo(42m));
                Assert.That(row.ConversionMessage, Is.EqualTo("Converting to M4B - 1 of 2"));
                Assert.That(row.ConvertToQuality, Is.EqualTo("M4B"));
                Assert.That(row.CanCancelConversion, Is.True);
                Assert.That(row.TargetBookIds, Is.EquivalentTo(new[] { book.Id }));

                // The cancel endpoint resolves the row by its synthesised id before cancelling.
                Assert.That(queue.Find(row.Id)?.DownloadId, Is.EqualTo(downloadId));
            }
            finally
            {
                tracking.Clear(downloadId);
            }
        }

        [Test]
        public void should_not_surface_an_untracked_conversion_without_a_live_status()
        {
            var (queue, tracking, downloadId) = CreateSubject();

            try
            {
                tracking.RegisterUntracked(downloadId, new UntrackedConversionContext
                {
                    Author = new Author { Id = 11, Name = "David Archer" },
                    Book = new Book { Id = 22, Title = "Black Sheep" },
                    Title = "Black Sheep",
                    Added = DateTime.UtcNow
                });

                Assert.That(FindRow(queue, downloadId), Is.Null);
            }
            finally
            {
                tracking.Clear(downloadId);
            }
        }

        [Test]
        public void should_drop_the_queue_row_once_the_conversion_completes()
        {
            var (queue, tracking, downloadId) = CreateSubject();

            try
            {
                tracking.RegisterUntracked(downloadId, new UntrackedConversionContext
                {
                    Author = new Author { Id = 11, Name = "David Archer" },
                    Book = new Book { Id = 22, Title = "Black Sheep" },
                    Title = "Black Sheep",
                    Added = DateTime.UtcNow
                });

                tracking.Start(downloadId, Quality.M4B.Id, "M4B", "Converting to M4B");
                Assert.That(FindRow(queue, downloadId), Is.Not.Null);

                tracking.Complete(downloadId);
                Assert.That(FindRow(queue, downloadId), Is.Null);
            }
            finally
            {
                tracking.Clear(downloadId);
            }
        }

        [Test]
        public void should_keep_the_queue_row_visible_after_a_failed_conversion()
        {
            var (queue, tracking, downloadId) = CreateSubject();

            try
            {
                tracking.RegisterUntracked(downloadId, new UntrackedConversionContext
                {
                    Author = new Author { Id = 11, Name = "David Archer" },
                    Book = new Book { Id = 22, Title = "Black Sheep" },
                    Title = "Black Sheep",
                    Added = DateTime.UtcNow
                });

                tracking.Start(downloadId, Quality.M4B.Id, "M4B", "Converting to M4B");
                tracking.Fail(downloadId, "synthetic conversion failure");

                var row = FindRow(queue, downloadId);

                Assert.That(row, Is.Not.Null);
                Assert.That(row.ConversionStatus, Is.EqualTo("failed"));
                Assert.That(row.ConversionMessage, Is.EqualTo("synthetic conversion failure"));
                Assert.That(row.CanCancelConversion, Is.False);
            }
            finally
            {
                tracking.Clear(downloadId);
            }
        }
    }
}
