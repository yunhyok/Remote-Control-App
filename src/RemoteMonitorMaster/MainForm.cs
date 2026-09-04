using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Media;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RemoteMonitorMaster
{
    internal sealed class MainForm : Form
    {
        private enum MonitorState
        {
            Unbound,
            Binding,
            Bound,
            Armed,
            Disarmed
        }

        private readonly AuditLog log;
        private readonly Timer bindTimer = new Timer();
        private readonly Timer pollTimer = new Timer();
        private readonly Label stateLabel = new Label();
        private readonly Label instructionLabel = new Label();
        private readonly Button bindButton = new Button();
        private readonly CheckBox confirmBox = new CheckBox();
        private readonly Button armButton = new Button();
        private readonly Button disarmButton = new Button();
        private readonly Button openLogButton = new Button();
        private readonly TextBox detailsBox = new TextBox();
        private readonly TextBox logPathBox = new TextBox();

        private MonitorState state = MonitorState.Unbound;
        private AutomationTarget target;
        private TokenStore tokenStore;
        private bool polling;
        private volatile bool stopRequested = true;
        private int activeWorkers;
        private int operationGeneration;
        private bool closeRequested;
        private bool allowClose;

        public MainForm(AuditLog log)
        {
            this.log = log;
            Text = AppInfo.Title;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(900, 500);
            MinimumSize = new Size(760, 460);
            Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            FormClosing += MainFormClosing;

            BuildUi();
            bindTimer.Interval = 3000;
            bindTimer.Tick += BindTimerTick;
            pollTimer.Interval = 1500;
            pollTimer.Tick += PollTimerTick;
            SetState(MonitorState.Unbound, "APP_READY");
        }

        private void BuildUi()
        {
            var title = new Label
            {
                Text = AppInfo.Title,
                Font = new Font(Font, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(18, 16)
            };

            stateLabel.AutoSize = true;
            stateLabel.Font = new Font(Font, FontStyle.Bold);
            stateLabel.Location = new Point(18, 47);

            instructionLabel.Text = "1) Open your KI-Messenger self-chat.  2) Click Bind, then Alt+Tab to it within 3 seconds.  3) Return here, confirm, and Arm.";
            instructionLabel.AutoSize = false;
            instructionLabel.Location = new Point(18, 76);
            instructionLabel.Size = new Size(860, 42);

            bindButton.Text = "Bind Foreground (3s)";
            bindButton.Location = new Point(18, 121);
            bindButton.Size = new Size(175, 32);
            bindButton.AccessibleDescription = "After three seconds, bind the current foreground window.";
            bindButton.Click += BeginBind;

            confirmBox.Text = "I confirm the bound window is my open KI-Messenger self-chat.";
            confirmBox.Location = new Point(210, 126);
            confirmBox.Size = new Size(400, 24);
            confirmBox.CheckedChanged += delegate { UpdateButtons(); };

            armButton.Text = "Arm";
            armButton.Location = new Point(620, 121);
            armButton.Size = new Size(95, 32);
            armButton.Click += ArmClick;

            disarmButton.Text = "Disarm";
            disarmButton.Location = new Point(725, 121);
            disarmButton.Size = new Size(95, 32);
            disarmButton.Click += delegate { Disarm("MANUAL_DISARM"); };

            var detailsLabel = new Label { Text = "Bound identity (chat header Name is redacted):", AutoSize = true, Location = new Point(18, 168) };
            detailsBox.Location = new Point(18, 190);
            detailsBox.Size = new Size(802, 185);
            detailsBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            detailsBox.Multiline = true;
            detailsBox.ReadOnly = true;
            detailsBox.ScrollBars = ScrollBars.Vertical;
            detailsBox.Text = "Not bound.";

            var logLabel = new Label { Text = "Log file:", AutoSize = true, Location = new Point(18, 393) };
            logPathBox.Location = new Point(18, 414);
            logPathBox.Size = new Size(697, 24);
            logPathBox.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            logPathBox.ReadOnly = true;
            logPathBox.Text = log.FilePath;

            openLogButton.Text = "Open Log Folder";
            openLogButton.Location = new Point(725, 409);
            openLogButton.Size = new Size(135, 32);
            openLogButton.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
            openLogButton.Click += OpenLogFolder;

            var privacy = new Label
            {
                Text = "Privacy: logs omit full conversations, attachment names, screenshots, and raw non-command text.",
                AutoSize = true,
                Location = new Point(18, 457),
                Anchor = AnchorStyles.Left | AnchorStyles.Bottom
            };

            Controls.AddRange(new Control[]
            {
                title, stateLabel, instructionLabel, bindButton, confirmBox, armButton, disarmButton,
                detailsLabel, detailsBox, logLabel, logPathBox, openLogButton, privacy
            });
        }

        private void BeginBind(object sender, EventArgs e)
        {
            stopRequested = true;
            operationGeneration++;
            pollTimer.Stop();
            target = null;
            tokenStore = null;
            confirmBox.Checked = false;
            detailsBox.Text = "Switch to the KI-Messenger self-chat now. Capturing foreground window in 3 seconds...";
            SetState(MonitorState.Binding, "BIND_COUNTDOWN_STARTED");
            bindTimer.Start();
        }

        private async void BindTimerTick(object sender, EventArgs e)
        {
            bindTimer.Stop();
            var window = NativeMethods.GetForegroundWindow();
            if (window == IntPtr.Zero || window == Handle)
            {
                FailAndDisarm("BIND_SELF_OR_NO_WINDOW", "Foreground capture did not find a separate application window.", null);
                return;
            }

            var generation = operationGeneration;
            WorkerStarted();
            try
            {
                log.Write("INFO", "BIND_CAPTURED", AuditLog.Field("hwnd", "0x" + window.ToInt64().ToString("X")));
                var bound = await Task.Run(() => AutomationTarget.Bind(window, log));
                if (generation != operationGeneration)
                {
                    throw new MonitorException("DISARM_REQUESTED", "Binding completed after Disarm; the result was discarded.");
                }

                target = bound;
                detailsBox.Text = bound.Summary;
                SetState(MonitorState.Bound, "BIND_COMPLETE");
                SystemSounds.Asterisk.Play();
            }
            catch (Exception ex)
            {
                var monitor = ex as MonitorException;
                FailAndDisarm(monitor == null ? "BIND_FAILED" : monitor.ReasonCode, ex.Message, ex);
            }
            finally
            {
                WorkerFinished();
            }
        }

        private async void ArmClick(object sender, EventArgs e)
        {
            if (target == null || state != MonitorState.Bound || !confirmBox.Checked)
            {
                return;
            }

            armButton.Enabled = false;
            var bound = target;
            var generation = operationGeneration;
            WorkerStarted();
            try
            {
                var result = await Task.Run(() =>
                {
                    var statePath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "RemoteMonitorMaster",
                        "state",
                        "seen-tokens.txt");
                    var store = new TokenStore(statePath);
                    var baseline = bound.ReadPingTokens(log, "ARM_BASELINE");
                    var seeded = baseline.Count(store.TryReserve);
                    return new ArmResult(store, baseline.Count, seeded);
                });

                if (generation != operationGeneration)
                {
                    throw new MonitorException("DISARM_REQUESTED", "Arming completed after Disarm; the result was discarded.");
                }

                tokenStore = result.Store;
                stopRequested = false;
                SetState(MonitorState.Armed, "ARM_COMPLETE");
                log.Write("INFO", "ARM_BASELINE_COMPLETE",
                    AuditLog.Field("ping_candidates", result.CandidateCount),
                    AuditLog.Field("newly_seeded", result.SeededCount));
                pollTimer.Start();
            }
            catch (Exception ex)
            {
                var monitor = ex as MonitorException;
                FailAndDisarm(monitor == null ? "ARM_FAILED" : monitor.ReasonCode, ex.Message, ex);
            }
            finally
            {
                WorkerFinished();
                UpdateButtons();
            }
        }

        private async void PollTimerTick(object sender, EventArgs e)
        {
            if (polling || state != MonitorState.Armed || target == null || tokenStore == null)
            {
                return;
            }

            polling = true;
            pollTimer.Stop();
            var bound = target;
            var store = tokenStore;
            var generation = operationGeneration;
            WorkerStarted();
            try
            {
                log.Write("INFO", "POLL_BEGIN");
                var outcome = await Task.Run(() => PollOnce(bound, store));
                if (generation != operationGeneration || state != MonitorState.Armed)
                {
                    return;
                }

                stateLabel.Text = "State: ARMED — last poll OK; sent " + outcome.Sent + ", duplicates " + outcome.Duplicates;
                log.Write("INFO", "POLL_OK",
                    AuditLog.Field("sent", outcome.Sent),
                    AuditLog.Field("duplicates", outcome.Duplicates));
            }
            catch (Exception ex)
            {
                if (generation == operationGeneration)
                {
                    var monitor = ex as MonitorException;
                    FailAndDisarm(monitor == null ? "POLL_FAILED" : monitor.ReasonCode, ex.Message, ex);
                }
                else
                {
                    var monitor = ex as MonitorException;
                    if (monitor != null && monitor.ReasonCode == "AUTOMATION_DRAFT_MAY_REMAIN")
                    {
                        detailsBox.Text = "DISARMED\r\nA generated PONG may remain in KI-Messenger. Inspect and clear the composer manually before using it.\r\n\r\nLog: " + log.FilePath;
                        stateLabel.Text = "State: DISARMED — AUTOMATION_DRAFT_MAY_REMAIN";
                        MessageBox.Show(
                            "A generated PONG may remain in KI-Messenger. Inspect and clear the composer manually before using it.",
                            AppInfo.Title,
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Warning);
                    }
                }
            }
            finally
            {
                polling = false;
                WorkerFinished();
                if (state == MonitorState.Armed)
                {
                    pollTimer.Start();
                }
            }
        }

        private PollOutcome PollOnce(AutomationTarget bound, TokenStore store)
        {
            var sent = 0;
            var duplicates = 0;
            var tokens = bound.ReadPingTokens(log, "POLL");
            foreach (var token in tokens)
            {
                if (stopRequested)
                {
                    throw new MonitorException("DISARM_REQUESTED", "Disarm or application close was requested.");
                }

                if (!store.TryReserve(token))
                {
                    duplicates++;
                    continue;
                }

                log.Write("INFO", "PING_CANDIDATE",
                    AuditLog.Field("token_sha256", TokenStore.Hash(token)),
                    AuditLog.Field("command_length", "!RM PING ".Length + token.Length));
                log.Write("INFO", "PING_RESERVED", AuditLog.Field("token_sha256", TokenStore.Hash(token)));
                bound.SendPong(log, token, () => stopRequested);
                sent++;
            }

            return new PollOutcome(sent, duplicates);
        }

        private void Disarm(string reason)
        {
            stopRequested = true;
            operationGeneration++;
            bindTimer.Stop();
            pollTimer.Stop();
            var bound = target;
            var sendCommitAlreadyStarted = false;
            if (bound != null)
            {
                sendCommitAlreadyStarted = bound.CancelWrites();
            }

            target = null;
            tokenStore = null;
            confirmBox.Checked = false;
            if (state != MonitorState.Unbound || reason == "APP_CLOSING")
            {
                var previous = state;
                state = MonitorState.Disarmed;
                var finalReason = sendCommitAlreadyStarted ? "SEND_COMMIT_ALREADY_STARTED" : reason;
                stateLabel.Text = "State: DISARMED — " + finalReason;
                try
                {
                    log.Write("INFO", "STATE_TRANSITION",
                        AuditLog.Field("from", previous),
                        AuditLog.Field("to", state),
                        AuditLog.Field("reason", finalReason));
                }
                catch
                {
                    // Disarm is complete even when the audit sink is unavailable.
                }

                UpdateButtons();

                if (sendCommitAlreadyStarted)
                {
                    detailsBox.Text = "DISARMED\r\nSend invocation had already crossed its commit boundary. Check the self-chat for one PONG; no later token will be processed.\r\n\r\nLog: " + log.FilePath;
                    MessageBox.Show(
                        "Send invocation had already started. Check the self-chat for one PONG; no later token will be processed.",
                        AppInfo.Title,
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
            }
        }

        private void FailAndDisarm(string reason, string message, Exception exception)
        {
            detailsBox.Text = "DISARMED\r\nReason: " + reason + "\r\n" + message + "\r\n\r\nLog: " + log.FilePath;
            Disarm(reason);

            try
            {
                if (exception == null)
                {
                    log.Write("ERROR", "FAIL_CLOSED", AuditLog.Field("reason", reason));
                }
                else
                {
                    log.WriteException("FAIL_CLOSED", exception, AuditLog.Field("reason", reason));
                }
            }
            catch
            {
                // State is already disarmed. Logging must never re-arm or restart polling.
            }
        }

        private void SetState(MonitorState next, string reason)
        {
            var previous = state;
            state = next;
            stateLabel.Text = "State: " + next.ToString().ToUpperInvariant() + " — " + reason;
            log.Write("INFO", "STATE_TRANSITION",
                AuditLog.Field("from", previous),
                AuditLog.Field("to", next),
                AuditLog.Field("reason", reason));
            UpdateButtons();
        }

        private void UpdateButtons()
        {
            var idle = activeWorkers == 0;
            bindButton.Enabled = idle && state != MonitorState.Binding && state != MonitorState.Armed;
            confirmBox.Enabled = idle && state == MonitorState.Bound;
            armButton.Enabled = idle && state == MonitorState.Bound && confirmBox.Checked;
            disarmButton.Enabled = state == MonitorState.Bound || state == MonitorState.Armed || state == MonitorState.Binding;
        }

        private void MainFormClosing(object sender, FormClosingEventArgs e)
        {
            if (!allowClose && activeWorkers > 0)
            {
                e.Cancel = true;
                closeRequested = true;
                Disarm("APP_CLOSING_WAIT_FOR_UIA");
                stateLabel.Text = "State: DISARMED — waiting for the active UIA operation to stop";
                return;
            }

            Disarm("APP_CLOSING");
        }

        private void WorkerStarted()
        {
            activeWorkers++;
            UpdateButtons();
        }

        private void WorkerFinished()
        {
            activeWorkers--;
            UpdateButtons();
            if (closeRequested && activeWorkers == 0)
            {
                allowClose = true;
                BeginInvoke(new Action(Close));
            }
        }

        private void OpenLogFolder(object sender, EventArgs e)
        {
            try
            {
                Process.Start("explorer.exe", log.FolderPath);
                log.Write("INFO", "LOG_FOLDER_OPENED");
            }
            catch (Exception ex)
            {
                try
                {
                    log.WriteException("LOG_FOLDER_OPEN_FAILED", ex);
                }
                catch
                {
                    // Still show the operator the folder path.
                }

                MessageBox.Show("Could not open the log folder.\r\n" + log.FolderPath, AppInfo.Title,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private sealed class ArmResult
        {
            public readonly TokenStore Store;
            public readonly int CandidateCount;
            public readonly int SeededCount;

            public ArmResult(TokenStore store, int candidateCount, int seededCount)
            {
                Store = store;
                CandidateCount = candidateCount;
                SeededCount = seededCount;
            }
        }

        private sealed class PollOutcome
        {
            public readonly int Sent;
            public readonly int Duplicates;

            public PollOutcome(int sent, int duplicates)
            {
                Sent = sent;
                Duplicates = duplicates;
            }
        }
    }
}
