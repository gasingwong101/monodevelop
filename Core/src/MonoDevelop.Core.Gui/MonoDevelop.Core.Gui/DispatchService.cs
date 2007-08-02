using System;
using System.Threading;
using System.Collections;

using MonoDevelop.Core;

namespace MonoDevelop.Core.Gui
{
	public class DispatchService : AbstractService
	{
		static ArrayList arrBackgroundQueue;
		static ArrayList arrGuiQueue;
		static Thread thrBackground;
		static uint iIdle = 0;
		static GLib.IdleHandler handler;
		static int guiThreadId;
		static GuiSyncContext guiContext;
		static internal bool DispatchDebug;
		const string errormsg = "An exception was thrown while dispatching a method call in the UI thread.";

		static DispatchService ()
		{
			guiContext = new GuiSyncContext ();
			
			guiThreadId = Thread.CurrentThread.ManagedThreadId;
			
			handler = new GLib.IdleHandler (guiDispatcher);
			arrBackgroundQueue = new ArrayList ();
			arrGuiQueue = new ArrayList ();
			thrBackground = new Thread (new ThreadStart (backgroundDispatcher));
			thrBackground.IsBackground = true;
			thrBackground.Priority = ThreadPriority.Lowest;
			thrBackground.Start ();
			DispatchDebug = Environment.GetEnvironmentVariable ("MONODEVELOP_DISPATCH_DEBUG") != null;
		}

		public static void GuiDispatch (MessageHandler cb)
		{
			QueueMessage (new GenericMessageContainer (cb, false));
		}

		public static void GuiDispatch (StatefulMessageHandler cb, object state)
		{
			QueueMessage (new StatefulMessageContainer (cb, state, false));
		}

		public static void GuiSyncDispatch (MessageHandler cb)
		{
			if (IsGuiThread) {
				cb ();
				return;
			}

			GenericMessageContainer mc = new GenericMessageContainer (cb, true);
			lock (mc) {
				QueueMessage (mc);
				Monitor.Wait (mc);
			}
			if (mc.Exception != null)
				throw new Exception (errormsg, mc.Exception);
		}
		
		public static void GuiSyncDispatch (StatefulMessageHandler cb, object state)
		{
			if (IsGuiThread) {
				cb (state);
				return;
			}

			StatefulMessageContainer mc = new StatefulMessageContainer (cb, state, true);
			lock (mc) {
				QueueMessage (mc);
				Monitor.Wait (mc);
			}
			if (mc.Exception != null)
				throw new Exception (errormsg, mc.Exception);
		}
		
		public static void RunPendingEvents ()
		{
			while (Gtk.Application.EventsPending ())
				Gtk.Application.RunIteration ();
			guiDispatcher ();
		}
		
		static void QueueMessage (object msg)
		{
			lock (arrGuiQueue) {
				arrGuiQueue.Add (msg);
				if (iIdle == 0)
					iIdle = GLib.Idle.Add (handler);
			}
		}
		
		public static bool IsGuiThread
		{
			get { return guiThreadId == Thread.CurrentThread.ManagedThreadId; }
		}
		
		public static void AssertGuiThread ()
		{
			if (guiThreadId != Thread.CurrentThread.ManagedThreadId)
				throw new InvalidOperationException ("This method can only be called in the GUI thread");
		}
		
		public static Delegate GuiDispatch (Delegate del)
		{
			return guiContext.CreateSynchronizedDelegate (del);
		}
		
		public static void BackgroundDispatch (MessageHandler cb)
		{
			arrBackgroundQueue.Add (new GenericMessageContainer (cb, false));
		}

		public static void BackgroundDispatch (StatefulMessageHandler cb, object state)
		{
			arrBackgroundQueue.Add (new StatefulMessageContainer (cb, state, false));
			//thrBackground.Resume ();
		}
		
		public static void ThreadDispatch (StatefulMessageHandler cb, object state)
		{
			StatefulMessageContainer smc = new StatefulMessageContainer (cb, state, false);
			Thread t = new Thread (new ThreadStart (smc.Run));
			t.IsBackground = true;
			t.Start ();
		}

		static bool guiDispatcher ()
		{
			GenericMessageContainer msg;
			int iterCount;
			
			lock (arrGuiQueue) {
				iterCount = arrGuiQueue.Count;
				if (iterCount == 0) {
					iIdle = 0;
					return false;
				}
			}
			
			for (int n=0; n<iterCount; n++) {
				lock (arrGuiQueue) {
					if (arrGuiQueue.Count == 0) {
						iIdle = 0;
						return false;
					}
					msg = (GenericMessageContainer) arrGuiQueue [0];
					arrGuiQueue.RemoveAt (0);
				}
				
				msg.Run ();
				
				if (msg.IsSynchronous)
					lock (msg) Monitor.PulseAll (msg);
				else if (msg.Exception != null)
					HandlerError (msg);
			}
			
			lock (arrGuiQueue) {
				if (arrGuiQueue.Count == 0) {
					iIdle = 0;
					return false;
				} else
					return true;
			}
		}

		static void backgroundDispatcher ()
		{
			// FIXME: use an event to avoid active wait
			while (true) {
				if (arrBackgroundQueue.Count == 0) {
					Thread.Sleep (500);
					//thrBackground.Suspend ();
					continue;
				}
				GenericMessageContainer msg = null;
				lock (arrBackgroundQueue) {
					msg = (GenericMessageContainer)arrBackgroundQueue[0];
					arrBackgroundQueue.RemoveAt (0);
				}
				if (msg != null) {
					msg.Run ();
					if (msg.Exception != null)
						HandlerError (msg);
				}
			}
		}
		
		static void HandlerError (GenericMessageContainer msg)
		{
			Runtime.LoggingService.Error (errormsg);
			Runtime.LoggingService.Error (msg.Exception);
			if (msg.CallerStack != null) {
				Runtime.LoggingService.Error ("\nCaller stack:");
				Runtime.LoggingService.Error (msg.CallerStack);
			}
			else
				Runtime.LoggingService.Error ("\n\nCaller stack not available. Define the environment variable MONODEVELOP_DISPATCH_DEBUG to enable caller stack capture.");
		}
	}

	public delegate void MessageHandler ();
	public delegate void StatefulMessageHandler (object state);

	class GenericMessageContainer
	{
		MessageHandler callback;
		protected Exception ex;
		protected bool isSynchronous;
		protected string callerStack;

		protected GenericMessageContainer () { }

		public GenericMessageContainer (MessageHandler cb, bool isSynchronous)
		{
			callback = cb;
			this.isSynchronous = isSynchronous;
			if (DispatchService.DispatchDebug) callerStack = Environment.StackTrace;
		}

		public virtual void Run ()
		{
			try {
				callback ();
			}
			catch (Exception e) {
				ex = e;
			}
		}
		
		public Exception Exception
		{
			get { return ex; }
		}
		
		public bool IsSynchronous
		{
			get { return isSynchronous; }
		}
		
		public string CallerStack
		{
			get { return callerStack; }
		}
	}

	class StatefulMessageContainer : GenericMessageContainer
	{
		object data;
		StatefulMessageHandler callback;

		public StatefulMessageContainer (StatefulMessageHandler cb, object state, bool isSynchronous)
		{
			data = state;
			callback = cb;
			this.isSynchronous = isSynchronous;
			if (DispatchService.DispatchDebug) callerStack = Environment.StackTrace;
		}
		
		public override void Run ()
		{
			try {
				callback (data);
			}
			catch (Exception e) {
				ex = e;
			}
		}
	}

}
