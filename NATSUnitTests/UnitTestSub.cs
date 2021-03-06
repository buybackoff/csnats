﻿// Copyright 2015 Apcera Inc. All rights reserved.

using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NATS.Client;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;

namespace NATSUnitTests
{
    /// <summary>
    /// Run these tests with the gnatsd auth.conf configuration file.
    /// </summary>
    [TestClass]
    public class TestSubscriptions
    {

        UnitTestUtilities utils = new UnitTestUtilities();

        [TestInitialize()]
        public void Initialize()
        {
            UnitTestUtilities.CleanupExistingServers();
            utils.StartDefaultServer();
        }

        [TestCleanup()]
        public void Cleanup()
        {
            utils.StopDefaultServer();
        }

        [TestMethod]
        public void TestServerAutoUnsub()
        {
            using (IConnection c = new ConnectionFactory().CreateConnection())
            {
                long received = 0;
                int max = 10;

                using (IAsyncSubscription s = c.SubscribeAsync("foo"))
                {
                    s.MessageHandler += (sender, arg) =>
                    {
                        System.Console.WriteLine("Received msg.");
                        received++;
                    };

                    s.AutoUnsubscribe(max);
                    s.Start();

                    for (int i = 0; i < (max * 2); i++)
                    {
                        c.Publish("foo", Encoding.UTF8.GetBytes("hello"));
                    }
                    c.Flush();

                    Thread.Sleep(500);

                    if (received != max)
                    {
                        Assert.Fail("Recieved ({0}) != max ({1})",
                            received, max);
                    }
                    Assert.IsFalse(s.IsValid);
                }
            }
        }

        [TestMethod]
        public void TestClientAutoUnsub()
        {
            using (IConnection c = new ConnectionFactory().CreateConnection())
            {
                long received = 0;
                int max = 10;

                using (ISyncSubscription s = c.SubscribeSync("foo"))
                {
                    s.AutoUnsubscribe(max);

                    for (int i = 0; i < max * 2; i++)
                    {
                        c.Publish("foo", null);
                    }
                    c.Flush();

                    Thread.Sleep(100);

                    try
                    {
                        while (true)
                        {
                            s.NextMessage(0);
                            received++;
                        }
                    }
                    catch (NATSMaxMessagesException) { /* ignore */ }

                    Assert.IsTrue(received == max);
                    Assert.IsFalse(s.IsValid);
                }
            }
        }

        [TestMethod]
        public void TestCloseSubRelease()
        {
            using (IConnection c = new ConnectionFactory().CreateConnection())
            {
                using (ISyncSubscription s = c.SubscribeSync("foo"))
                {
                    Stopwatch sw = new Stopwatch();
                    sw.Start();
                    try
                    {
                        new Task(() => { Thread.Sleep(100); c.Close(); }).Start();
                         s.NextMessage(10000);
                    }
                    catch (Exception) { /* ignore */ }

                    sw.Stop();

                    Assert.IsTrue(sw.ElapsedMilliseconds < 10000);
                }
            }
        }

        [TestMethod]
        public void TestValidSubscriber()
        {
            using (IConnection c = new ConnectionFactory().CreateConnection())
            {
                using (ISyncSubscription s = c.SubscribeSync("foo"))
                {
                    Assert.IsTrue(s.IsValid);

                    try { s.NextMessage(100); }
                    catch (NATSTimeoutException) { }

                    Assert.IsTrue(s.IsValid);

                    s.Unsubscribe();

                    Assert.IsFalse(s.IsValid);

                    try { s.NextMessage(100); }
                    catch (NATSBadSubscriptionException) { }
                }
            }
        }

        [TestMethod]
        public void TestSlowSubscriber()
        {
            Options opts = ConnectionFactory.GetDefaultOptions();
            opts.SubChannelLength = 10;

            using (IConnection c = new ConnectionFactory().CreateConnection(opts))
            {
                using (ISyncSubscription s = c.SubscribeSync("foo"))
                {
                    for (int i =0; i < (opts.SubChannelLength+100); i++)
                    {
                        c.Publish("foo", null);
                    }

                    try
                    {
                        c.Flush();
                    }
                    catch (Exception)
                    {
                        // ignore
                    }

                    try 
                    {
                        s.NextMessage();
                    }
                    catch (NATSSlowConsumerException)
                    {
                        return;
                    }
                    Assert.Fail("Did not receive an exception.");
                }
            } 
        }

        [TestMethod]
        public void TestSlowAsyncSubscriber()
        {
            ConditionalObj subCond = new ConditionalObj();

            Options opts = ConnectionFactory.GetDefaultOptions();
            opts.SubChannelLength = 100;

            using (IConnection c = new ConnectionFactory().CreateConnection(opts))
            {
                using (IAsyncSubscription s = c.SubscribeAsync("foo"))
                {
                    Object mu = new Object();

                    s.MessageHandler += (sender, args) =>
                    {
                        // block to back us up.
                        subCond.wait(2000);
                    };

                    s.Start();

                    Assert.IsTrue(s.PendingByteLimit == Defaults.SubPendingBytesLimit);
                    Assert.IsTrue(s.PendingMessageLimit == Defaults.SubPendingMsgsLimit);

                    long pml = 100;
                    long pbl = 1024*1024;

                    s.SetPendingLimits(pml, pbl);

                    Assert.IsTrue(s.PendingByteLimit == pbl);
                    Assert.IsTrue(s.PendingMessageLimit == pml);

                    for (int i = 0; i < (pml + 100); i++)
                    {
                        c.Publish("foo", null);
                    }

                    int flushTimeout = 5000;

                    Stopwatch sw = new Stopwatch();
                    sw.Start();

                    try
                    {
                        c.Flush(flushTimeout);
                    }
                    catch (Exception ex)
                    {
                        Assert.Fail("Flush failed." + ex);
                    }

                    sw.Stop();

                    subCond.notify();

                    if (sw.ElapsedMilliseconds >= flushTimeout)
                    {
                        Assert.Fail("elapsed ({0}) > timeout ({1})",
                            sw.ElapsedMilliseconds, flushTimeout);
                    }
                }
            }
        }

        [TestMethod]
        public void TestAsyncErrHandler()
        {
            Object subLock = new Object();
            object testLock = new Object();
            IAsyncSubscription s;


            Options opts = ConnectionFactory.GetDefaultOptions();
            opts.SubChannelLength = 10;

            bool handledError = false;

            using (IConnection c = new ConnectionFactory().CreateConnection(opts))
            {
                using (s = c.SubscribeAsync("foo"))
                {
                    c.Opts.AsyncErrorEventHandler = (sender, args) =>
                    {
                        lock (subLock)
                        {
                            if (handledError)
                                return;

                            handledError = true;

                            Assert.IsTrue(args.Subscription == s);

                            System.Console.WriteLine("Expected Error: " + args.Error);
                            Assert.IsTrue(args.Error.Contains("Slow"));

                            // release the subscriber
                            Monitor.Pulse(subLock);
                        }

                        // release the test
                        lock (testLock) { Monitor.Pulse(testLock); }
                    };

                    bool blockedOnSubscriber = false;
                    s.MessageHandler += (sender, args) =>
                    {
                        lock (subLock)
                        {
                            if (blockedOnSubscriber)
                                return;

                            Console.WriteLine("Subscriber Waiting....");
                            Assert.IsTrue(Monitor.Wait(subLock, 500));
                            Console.WriteLine("Subscriber done.");
                            blockedOnSubscriber = true;
                        }
                    };

                    s.Start();

                    lock(testLock)
                    {

                        for (int i = 0; i < (opts.SubChannelLength + 100); i++)
                        {
                            c.Publish("foo", null);
                        }
 
                        try
                        {
                            c.Flush(1000);
                        }
                        catch (Exception)
                        {
                            // ignore - we're testing the error handler, not flush.
                        }

                        Assert.IsTrue(Monitor.Wait(testLock, 1000));
                    }
                }
            }
        }

        [TestMethod]
        public void TestAsyncSubscriberStarvation()
        {
            Object waitCond = new Object();

            using (IConnection c = new ConnectionFactory().CreateConnection())
            {
                using (IAsyncSubscription helper = c.SubscribeAsync("helper"),
                                          start = c.SubscribeAsync("start"))
                {
                    helper.MessageHandler += (sender, arg) =>
                    {
                        System.Console.WriteLine("Helper");
                        c.Publish(arg.Message.Reply,
                            Encoding.UTF8.GetBytes("Hello"));
                    };
                    helper.Start();

                    start.MessageHandler += (sender, arg) =>
                    {
                        System.Console.WriteLine("Responsder");
		                string responseIB = c.NewInbox();
                        IAsyncSubscription ia = c.SubscribeAsync(responseIB);

                        ia.MessageHandler += (iSender, iArgs) =>
                        {
                            System.Console.WriteLine("Internal subscriber.");
                            lock (waitCond) { Monitor.Pulse(waitCond); }
                        };
                        ia.Start();
 
		                c.Publish("helper", responseIB,
                            Encoding.UTF8.GetBytes("Help me!"));
                    };

                    start.Start();
                     
                    c.Publish("start", Encoding.UTF8.GetBytes("Begin"));
                    c.Flush();

                    lock (waitCond) 
                    { 
                        Assert.IsTrue(Monitor.Wait(waitCond, 2000));
                    }
                }
            }
        }


        [TestMethod]
        public void TestAsyncSubscribersOnClose()
        {
            /// basically tests if the subscriber sub channel gets
            /// cleared on a close.
            Object waitCond = new Object();
            int callbacks = 0;

            using (IConnection c = new ConnectionFactory().CreateConnection())
            {
                using (IAsyncSubscription s = c.SubscribeAsync("foo"))
                {
                    s.MessageHandler += (sender, args) =>
                    {
                        callbacks++;
                        lock (waitCond)
                        {
                            Monitor.Wait(waitCond);
                        }
                    };

                    s.Start();

                    for (int i = 0; i < 10; i++)
                    {
                        c.Publish("foo", null);
                    }
                    c.Flush();

                    Thread.Sleep(500);
                    c.Close();

                    lock (waitCond)
                    {
                        Monitor.Pulse(waitCond);
                    }

                    Thread.Sleep(500);

                    Assert.IsTrue(callbacks == 1);
                }
            }
        }

        [TestMethod]
        public void TestNextMessageOnClosedSub()
        {
            using (IConnection c = new ConnectionFactory().CreateConnection())
            {
                ISyncSubscription s = c.SubscribeSync("foo");
                s.Unsubscribe();

                try
                {
                    s.NextMessage();
                }
                catch (NATSBadSubscriptionException) { } // ignore.

                // any other exceptions will fail the test.
            }
        }

        [TestMethod]
        public void TestAsyncSubscriptionPending()
        {
            int total = 100;
            int receivedCount = 0;

            ConditionalObj subDoneCond = new ConditionalObj();
            ConditionalObj startProcessing = new ConditionalObj();

            byte[] data = System.Text.Encoding.UTF8.GetBytes("0123456789");

            using (IConnection c = new ConnectionFactory().CreateConnection())
            {
                ISubscription s = c.SubscribeAsync("foo", (sender, args) =>
                {
                    startProcessing.wait(60000);

                    receivedCount++;
                    if (receivedCount == total)
                    {
                        subDoneCond.notify();
                    }
                });

                for (int i = 0; i < total; i++)
                {
                    c.Publish("foo", data);
                }
                c.Flush();

                Thread.Sleep(1000);

                int expectedPendingCount = total - 1;

                Assert.IsTrue(s.QueuedMessageCount == expectedPendingCount);

                Assert.IsTrue((s.MaxPendingBytes == (data.Length * total)) ||
                    (s.MaxPendingBytes == (data.Length * expectedPendingCount)));
                Assert.IsTrue((s.MaxPendingMessages == total) ||
                    (s.MaxPendingMessages == expectedPendingCount));
                Assert.IsTrue((s.PendingBytes == (data.Length * total)) ||
                    (s.PendingBytes == (data.Length * expectedPendingCount)));

                long pendingBytes;
                long pendingMsgs;

                s.GetPending(out pendingBytes, out pendingMsgs);
                Assert.IsTrue(pendingBytes == s.PendingBytes);
                Assert.IsTrue(pendingMsgs == s.PendingMessages);

                long maxPendingBytes;
                long maxPendingMsgs;
                s.GetMaxPending(out maxPendingBytes, out maxPendingMsgs);
                Assert.IsTrue(maxPendingBytes == s.MaxPendingBytes);
                Assert.IsTrue(maxPendingMsgs == s.MaxPendingMessages);


                Assert.IsTrue((s.PendingMessages == total) ||
                    (s.PendingMessages == expectedPendingCount));

                Assert.IsTrue(s.Delivered == 1);
                Assert.IsTrue(s.Dropped == 0);

                startProcessing.notify();

                subDoneCond.wait(1000);

                Assert.IsTrue(s.QueuedMessageCount == 0);

                Assert.IsTrue((s.MaxPendingBytes == (data.Length * total)) ||
                    (s.MaxPendingBytes == (data.Length * expectedPendingCount)));
                Assert.IsTrue((s.MaxPendingMessages == total) ||
                    (s.MaxPendingMessages == expectedPendingCount));

                Assert.IsTrue(s.PendingMessages == 0);
                Assert.IsTrue(s.PendingBytes == 0);

                Assert.IsTrue(s.Delivered == total);
                Assert.IsTrue(s.Dropped == 0);

                s.Unsubscribe();

                try
                {
                    long i = s.MaxPendingBytes;
                    Assert.Fail("Should have receieved an exception.");
                }
                catch (Exception) { }
                try
                {
                    long i = s.MaxPendingMessages;
                    Assert.Fail("Should have receieved an exception.");
                }
                catch (Exception) { }
                try
                {
                    long i = s.PendingMessageLimit;
                    Assert.Fail("Should have receieved an exception.");
                }
                catch (Exception) { }
                try
                {
                    long i = s.PendingByteLimit;
                    Assert.Fail("Should have receieved an exception.");
                }
                catch (Exception) { }
                try
                {
                    s.SetPendingLimits(1, 10);
                    Assert.Fail("Should have receieved an exception.");
                }
                catch (Exception) { }
                try
                {
                    s.ClearMaxPending();
                    Assert.Fail("Should have receieved an exception.");
                }
                catch (Exception) { }
                try
                {
                    long i = s.Delivered;
                    Assert.Fail("Should have receieved an exception.");
                }
                catch (Exception) { }
                try
                {
                    long i = s.Dropped;
                    Assert.Fail("Should have receieved an exception.");
                }
                catch (Exception) { }
            }
        }

        [TestMethod]
        public void TestSyncSubscriptionPending()
        {
            int total = 100;

            ConditionalObj subDoneCond = new ConditionalObj();
            ConditionalObj startProcessing = new ConditionalObj();

            byte[] data = System.Text.Encoding.UTF8.GetBytes("0123456789");

            using (IConnection c = new ConnectionFactory().CreateConnection())
            {
                ISyncSubscription s = c.SubscribeSync("foo");

                for (int i = 0; i < total; i++)
                {
                    c.Publish("foo", data);
                }
                c.Flush();

                Assert.IsTrue(s.QueuedMessageCount == total);

                Assert.IsTrue((s.MaxPendingBytes == (data.Length * total)) ||
                    (s.MaxPendingBytes == (data.Length * total)));
                Assert.IsTrue((s.MaxPendingMessages == total) ||
                    (s.MaxPendingMessages == total));

                Assert.IsTrue(s.Delivered == 0);
                Assert.IsTrue(s.Dropped == 0);

                for (int i = 0; i < total; i++)
                {
                    s.NextMessage();
                }

                Assert.IsTrue(s.QueuedMessageCount == 0);

                Assert.IsTrue((s.MaxPendingBytes == (data.Length * total)) ||
                    (s.MaxPendingBytes == (data.Length * total)));
                Assert.IsTrue((s.MaxPendingMessages == total) ||
                    (s.MaxPendingMessages == total));

                Assert.IsTrue(s.Delivered == total);
                Assert.IsTrue(s.Dropped == 0);

                s.Unsubscribe();
            }        
        }

        [TestMethod]
        public void TestAsyncSubscriptionPendingDrain()
        {
            int total = 100;

            byte[] data = System.Text.Encoding.UTF8.GetBytes("0123456789");

            using (IConnection c = new ConnectionFactory().CreateConnection())
            {
                ISubscription s = c.SubscribeAsync("foo", (sender, args) => {});

                for (int i = 0; i < total; i++)
                {
                    c.Publish("foo", data);
                }
                c.Flush();

                while (s.Delivered != total)
                {
                    Thread.Sleep(50);
                }

                Assert.IsTrue(s.Dropped == 0);
                Assert.IsTrue(s.PendingBytes == 0);
                Assert.IsTrue(s.PendingMessages == 0);

                s.Unsubscribe();
            }
        }

        [TestMethod]
        public void TestSyncSubscriptionPendingDrain()
        {
            int total = 100;

            byte[] data = System.Text.Encoding.UTF8.GetBytes("0123456789");

            using (IConnection c = new ConnectionFactory().CreateConnection())
            {
                ISyncSubscription s = c.SubscribeSync("foo");

                for (int i = 0; i < total; i++)
                {
                    c.Publish("foo", data);
                }
                c.Flush();

                while (s.Delivered != total)
                {
                    s.NextMessage(100);
                }

                Assert.IsTrue(s.Dropped == 0);
                Assert.IsTrue(s.PendingBytes == 0);
                Assert.IsTrue(s.PendingMessages == 0);

                s.Unsubscribe();
            }
        }

    }
}

