using System;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PlayniteAchievements.Tests.TestInfrastructure
{
    /// <summary>
    /// Runs WPF test bodies on a dedicated STA thread and hosts their visuals in windows that never
    /// reach the desktop.
    /// </summary>
    internal static class StaTestHost
    {
        private const double OffscreenCoordinate = -10000d;

        /// <summary>
        /// Runs <paramref name="action"/> on a new STA thread and shuts its dispatcher down afterwards,
        /// so no WPF thread outlives the test. Assertion outcomes (failed, inconclusive) propagate as
        /// thrown; any other exception becomes a failure carrying its full text.
        /// </summary>
        public static void Run(Action action)
        {
            Exception error = null;
            var thread = new Thread(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    error = ex is TargetInvocationException invocationException && invocationException.InnerException != null
                        ? invocationException.InnerException
                        : ex;
                }
                finally
                {
                    Dispatcher.CurrentDispatcher.InvokeShutdown();
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            if (error is UnitTestAssertException)
            {
                ExceptionDispatchInfo.Capture(error).Throw();
            }

            if (error != null)
            {
                throw new AssertFailedException(error.ToString());
            }
        }

        /// <summary>
        /// Shows <paramref name="content"/> in a window placed off every monitor, kept out of the taskbar
        /// and never activated, so layout and rendering run without taking focus. The default window
        /// chrome is kept so the client area matches an ordinary window of the same size.
        /// </summary>
        public static Window ShowOffscreen(object content, double width, double height)
        {
            var window = new Window
            {
                Width = width,
                Height = height,
                Content = content,
                Left = OffscreenCoordinate,
                Top = OffscreenCoordinate,
                ShowInTaskbar = false,
                ShowActivated = false
            };

            window.Show();
            return window;
        }
    }
}
