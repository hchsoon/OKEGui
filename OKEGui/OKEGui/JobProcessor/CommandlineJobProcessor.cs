﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace OKEGui
{
    public enum StreamType : ushort { None = 0, Stderr = 1, Stdout = 2 }

    public abstract class CommandlineJobProcessor : IJobProcessor
    {
        #region variables

        protected Job job;
        protected DateTime startTime;
        protected bool isProcessing = false;
        protected Process proc = new Process(); // the encoder process
        protected string executable; // path and filename of the commandline encoder to be used
        protected ManualResetEvent mre = new ManualResetEvent(true); // lock used to pause encoding
        protected ManualResetEvent finishMre = new ManualResetEvent(false);
        protected ManualResetEvent stdoutDone = new ManualResetEvent(false);
        protected ManualResetEvent stderrDone = new ManualResetEvent(false);
        protected TaskStatus su;
        protected Thread readFromStdErrThread;
        protected Thread readFromStdOutThread;
        protected List<string> tempFiles = new List<string>();
        protected bool bRunSecondTime = false;
        protected bool bWaitForExit = false;

        //protected LogItem log;
        //protected LogItem stdoutLog;
        //protected LogItem stderrLog;

        #endregion variables

        #region temp file utils

        protected void writeTempTextFile(string filePath, string text)
        {
            using (Stream temp = new FileStream(filePath, System.IO.FileMode.Create))
            {
                using (TextWriter avswr = new StreamWriter(temp, System.Text.Encoding.Default))
                {
                    avswr.WriteLine(text);
                }
            }
            tempFiles.Add(filePath);
        }

        private void deleteTempFiles()
        {
            foreach (string filePath in tempFiles)
                safeDelete(filePath);
        }

        private static void safeDelete(string filePath)
        {
            try
            {
                File.Delete(filePath);
            }
            catch
            {
                // Do Nothing
            }
        }

        #endregion temp file utils

        // returns true if the exit code yields a meaningful answer
        protected virtual bool checkExitCode
        {
            get { return true; }
        }

        protected virtual void getErrorLine()
        {
            return;
        }

        public abstract string Commandline
        {
            get;
        }

        /// <summary>
        /// handles the encoder process existing
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        protected void proc_Exited(object sender, EventArgs e)
        {
            mre.Set();  // Make sure nothing is waiting for pause to stop
            stdoutDone.WaitOne(); // wait for stdout to finish processing
            stderrDone.WaitOne(); // wait for stderr to finish processing

            // check the exitcode
            if (checkExitCode && proc.ExitCode != 0)
            {
                getErrorLine();
                string strError = WindowUtil.GetErrorText(proc.ExitCode);
                if (!su.WasAborted)
                {
                    su.HasError = true;
                    // log.LogEvent("Process exits with error: " + strError, ImageType.Error);
                }
                else
                {
                    // log.LogEvent("Process exits with error: " + strError);
                }
            }

            if (bRunSecondTime)
            {
                bRunSecondTime = false;
                Start();
            }
            else
            {
                su.IsComplete = true;
                StatusUpdate(su);
            }

            bWaitForExit = false;
        }

        #region IVideoEncoder overridden Members

        public abstract void Setup(Job job, TaskStatus su);

        public void Start()
        {
            proc = new Process();
            ProcessStartInfo pstart = new ProcessStartInfo();
            pstart.FileName = executable;
            pstart.Arguments = Commandline;
            pstart.RedirectStandardOutput = true;
            pstart.RedirectStandardError = true;
            pstart.WindowStyle = ProcessWindowStyle.Minimized;
            pstart.CreateNoWindow = true;
            pstart.UseShellExecute = false;
            proc.StartInfo = pstart;
            //proc.EnableRaisingEvents = true;
            //proc.Exited += new EventHandler(proc_Exited);
            bWaitForExit = false;
            // log.LogValue("Job command line", '"' + pstart.FileName + "\" " + pstart.Arguments);

            try
            {
                bool started = proc.Start();
                // startTime = DateTime.Now;
                isProcessing = true;
                // log.LogEvent("Process started");
                // stdoutLog = log.Info(string.Format("[{0:G}] {1}", DateTime.Now, "Standard output stream"));
                // stderrLog = log.Info(string.Format("[{0:G}] {1}", DateTime.Now, "Standard error stream"));
                readFromStdErrThread = new Thread(new ThreadStart(readStdErr));
                readFromStdOutThread = new Thread(new ThreadStart(readStdOut));
                readFromStdOutThread.Start();
                readFromStdErrThread.Start();
                // new System.Windows.Forms.MethodInvoker(this.RunStatusCycle).BeginInvoke(null, null);
                // this.changePriority(MainForm.Instance.Settings.ProcessingPriority);
                // this.changePriority(ProcessPriority.NORMAL);
            }
            catch (Exception e)
            {
                throw e;
            }
        }

        public void Stop()
        {
            if (proc != null && !proc.HasExited)
            {
                try
                {
                    bWaitForExit = true;
                    mre.Set(); // if it's paused, then unpause
                    su.WasAborted = true;
                    proc.Kill();
                    while (bWaitForExit) // wait until the process has terminated without locking the GUI
                    {
                        System.Windows.Forms.Application.DoEvents();
                        System.Threading.Thread.Sleep(100);
                    }
                    proc.WaitForExit();
                    return;
                }
                catch (Exception e)
                {
                    throw e;
                }
            }
            else
            {
                if (proc == null)
                    throw new Exception("Encoder process does not exist");
                else
                    throw new Exception("Encoder process has already existed");
            }
        }

        public void Pause()
        {
            if (!canPause)
                throw new Exception("Can't pause this kind of job.");
            if (!mre.Reset())
                throw new Exception("Could not reset mutex. pause failed");
        }

        public void Resume()
        {
            if (!canPause)
                throw new Exception("Can't resume this kind of job.");
            if (!mre.Set())
                throw new Exception("Could not set mutex. pause failed");
        }

        public virtual void WaitForFinish()
        {
            finishMre.WaitOne();
        }

        public void SetFinish()
        {
            finishMre.Set();
        }

        public bool IsRunning()
        {
            return (proc != null && !proc.HasExited);
        }

        public void ChangePriority(ProcessPriority priority)
        {
            if (IsRunning())
            {
                try
                {
                    switch (priority)
                    {
                        case ProcessPriority.IDLE:
                            proc.PriorityClass = ProcessPriorityClass.Idle;
                            break;

                        case ProcessPriority.BELOW_NORMAL:
                            proc.PriorityClass = ProcessPriorityClass.BelowNormal;
                            break;

                        case ProcessPriority.NORMAL:
                            proc.PriorityClass = ProcessPriorityClass.Normal;
                            break;

                        case ProcessPriority.ABOVE_NORMAL:
                            proc.PriorityClass = ProcessPriorityClass.AboveNormal;
                            break;

                        case ProcessPriority.HIGH:
                            proc.PriorityClass = ProcessPriorityClass.RealTime;
                            break;
                    }
                    VistaStuff.SetProcessPriority(proc.Handle, proc.PriorityClass);
                    return;
                }
                catch (Exception e) // process could not be running anymore
                {
                    throw e;
                }
            }
            else
            {
                if (proc == null)
                    throw new Exception("Process has not been started yet");
                else
                {
                    Debug.Assert(proc.HasExited);
                    throw new Exception("Process has exited");
                }
            }
        }

        public virtual bool canPause
        {
            get { return true; }
        }

        #endregion IVideoEncoder overridden Members

        #region reading process output

        public virtual string Executable
        {
            get { return executable; }
        }

        protected virtual void readStream(StreamReader sr, ManualResetEvent rEvent, StreamType str)
        {
            string line;
            if (proc != null)
            {
                try
                {
                    while ((line = sr.ReadLine()) != null)
                    {
                        mre.WaitOne();

                        Debugger.Log(0, "readStream", line + "\n");
                        ProcessLine(line, str);
                    }
                }
                catch (Exception e)
                {
                    ProcessLine("Exception in readStream. Line cannot be processed. " + e.Message, str);
                    throw e;
                }
                rEvent.Set();
            }
        }

        protected void readStdOut()
        {
            StreamReader sr = null;
            try
            {
                sr = proc.StandardOutput;
            }
            catch (Exception e)
            {
                // log.LogValue("Exception getting IO reader for stdout", e, ImageType.Error);
                stdoutDone.Set();
                return;
            }
            readStream(sr, stdoutDone, StreamType.Stdout);
        }

        protected void readStdErr()
        {
            StreamReader sr = null;
            try
            {
                sr = proc.StandardError;
            }
            catch (Exception e)
            {
                // log.LogValue("Exception getting IO reader for stderr", e, ImageType.Error);
                stderrDone.Set();
                return;
            }
            readStream(sr, stderrDone, StreamType.Stderr);
        }

        public virtual void ProcessLine(string line, StreamType stream)
        {
            if (String.IsNullOrEmpty(line.Trim()))
                return;

            //if (stream == StreamType.Stdout)
            //    stdoutLog.LogEvent(line, oType);
            //if (stream == StreamType.Stderr)
            //    stderrLog.LogEvent(line, oType);

            //if (oType == ImageType.Error)
            //    su.HasError = true;
        }

        #endregion reading process output

        #region status updates

        public event JobProcessingStatusUpdateCallback StatusUpdate;

        protected void RunStatusCycle()
        {
            while (IsRunning())
            {
                su.TimeElapsed = DateTime.Now - startTime;

                doStatusCycleOverrides();

                if (StatusUpdate != null && proc != null && !proc.HasExited)
                    StatusUpdate(su);

                Thread.Sleep(1000);
            }
        }

        protected virtual void doStatusCycleOverrides()
        { }

        #endregion status updates
    }
}
