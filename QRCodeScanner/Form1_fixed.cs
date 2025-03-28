using System.Windows.Forms;
using System.Configuration;
using System.Diagnostics;
using System.Runtime.InteropServices; // For P/Invoke
using System.Text; // For StringBuilder
using System.Linq;
using System.IO;
using System.Threading;
using System.IO.Ports; // Add for SerialPort
using System.Threading.Tasks; // Add for Task
using Microsoft.Win32; // For SystemEvents

namespace QRCodeScanner;

public partial class Form1 : Form
{
    // Remove keyboard hook constants and variables
    private StringBuilder _scanBuffer = new StringBuilder();
    
    private NotifyIcon trayIcon;
    private TextBox inputBox;
    private TextBox displayBox;
    private TextBox jobInfoBox;
    private TextBox materialInfoBox;
    private string currentInput = "";
    private System.Windows.Forms.Timer inputTimer;
    private const string SETTINGS_FILE = "settings.ini";
    
    // Hardcoded file paths
    private const string MOZAIK_SETTINGS_FOLDER = @"C:\Mozaik\Settings";
    private const string MOZAIK_STATE_DAT_PATH = @"C:\Mozaik\State.dat";
    private const string OPTIMIZE_EXE_PATH = @"C:\Mozaik\optimize.exe";

    // Add SerialPort for scanner
    private SerialPort _serialPort;
    private const string DEFAULT_COM_PORT = "COM3";
    private const int DEFAULT_BAUD_RATE = 9600;

    // New method to set up file system watcher to preserve case formatting
    private FileSystemWatcher? casePreservationWatcher = null;
    private System.Windows.Forms.Timer? periodicCaseCheckTimer = null;
    private string? lastJobName = null;
    private string? lastMaterial = null;

    // Add a debug mode flag at class level
    private bool _debugMode = false;

    // Add string buffer for accumulating data at class level
    private string _serialDataBuffer = "";
    private System.Windows.Forms.Timer _serialBufferTimer;

    // Add a flag to track if we're disposing
    private bool _isDisposing = false;
    
    // Add a list to track all timers for proper cleanup
    private List<System.Windows.Forms.Timer> _timers = new List<System.Windows.Forms.Timer>();
    
    // Add objects for thread synchronization
    private readonly object _fileLock = new object();
    private readonly object _serialBufferLock = new object();
    
    // Add a flag to track if application is shutting down
    private bool _isShuttingDown = false;

    // Add implementation for SerialBufferTimer_Tick
    private void SerialBufferTimer_Tick(object sender, EventArgs e)
    {
        if (_isDisposing) return;
        
        try
        {
            // Timer triggered - process any data in the buffer
            ProcessSerialBuffer(true); // Force processing due to timeout
            
            // Stop the timer until we receive more data
            _serialBufferTimer.Stop();
        }
        catch (Exception ex)
        {
            if (!_isDisposing)
            {
                HandleSerialException(ex, "Error in serial buffer timer");
            }
        }
    }

    // Add new event handler for serial errors
    private void SerialPort_ErrorReceived(object sender, SerialErrorReceivedEventArgs e)
    {
        if (_isDisposing) return;
        
        this.BeginInvoke(new Action(() =>
        {
            if (_isDisposing) return;
            
            try
            {
                SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Serial error received: {e.EventType}\r\n");
                
                // Try to recover from the error based on the type
                switch (e.EventType)
                {
                    case SerialError.Frame:
                    case SerialError.Overrun:
                    case SerialError.RXOver:
                    case SerialError.RXParity:
                        // Data errors - clear buffers
                        try
                        {
                            if (_serialPort != null && _serialPort.IsOpen)
                            {
                                _serialPort.DiscardInBuffer();
                                _serialPort.DiscardOutBuffer();
                                
                                // Also clear our internal buffer
                                lock (_serialBufferLock)
                                {
                                    if (!string.IsNullOrEmpty(_serialDataBuffer))
                                    {
                                        SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Cleared internal buffer after error\r\n");
                                        _serialDataBuffer = "";
                                    }
                                }
                                
                                SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Cleared serial buffers after error\r\n");
                            }
                        }
                        catch (Exception ex)
                        {
                            HandleSerialException(ex, "Error clearing buffers");
                        }
                        break;
                        
                    case SerialError.TXFull:
                        // Transmit buffer full - wait and retry
                        SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Transmit buffer full, waiting to clear\r\n");
                        Thread.Sleep(500);
                        break;
                    
                    default:
                        // For other errors, attempt reconnection
                        SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Unhandled serial error: {e.EventType}, attempting reconnection\r\n");
                        AttemptSerialReconnection();
                        break;
                }
            }
            catch (Exception ex)
            {
                HandleSerialException(ex, "Error handling serial error event");
            }
        }));
    }
    
    // Add new method for reconnection attempts
    private System.Windows.Forms.Timer reconnectionTimer;
    private string reconnectPortName;
    private int reconnectBaudRate;
    private Parity reconnectParity;
    private int reconnectDataBits;
    private StopBits reconnectStopBits;
    private int reconnectAttempts = 0;
    private const int MAX_RECONNECT_ATTEMPTS = 5;
    
    private void SetupReconnectionTimer(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits)
    {
        // Skip if shutting down
        if (_isDisposing) return;
        
        try
        {
            // Store connection parameters
            reconnectPortName = portName;
            reconnectBaudRate = baudRate;
            reconnectParity = parity;
            reconnectDataBits = dataBits;
            reconnectStopBits = stopBits;
            reconnectAttempts = 0;
            
            // Create or reconfigure the timer
            if (reconnectionTimer == null)
            {
                reconnectionTimer = new System.Windows.Forms.Timer();
                reconnectionTimer.Tick += ReconnectionTimer_Tick;
                
                // Track this timer
                _timers.Add(reconnectionTimer);
            }
            else
            {
                // Make sure it's stopped before changing interval
                if (reconnectionTimer.Enabled)
                {
                    reconnectionTimer.Stop();
                }
            }
            
            reconnectionTimer.Interval = 10000; // Try every 10 seconds
            if (!_isDisposing) // Check before UI update
            {
                SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Automatic reconnection will try every 10 seconds\r\n");
            }
            
            // Only start if not disposing
            if (!_isDisposing)
            {
                reconnectionTimer.Start();
            }
        }
        catch (Exception ex)
        {
            if (!_isDisposing)
            {
                SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Error setting up reconnection timer: {ex.Message}\r\n");
            }
            Debug.WriteLine($"Error in SetupReconnectionTimer: {ex.Message}");
        }
    }
    
    private void ReconnectionTimer_Tick(object sender, EventArgs e)
    {
        // Skip if shutting down
        if (_isDisposing)
        {
            if (reconnectionTimer != null && reconnectionTimer.Enabled)
            {
                reconnectionTimer.Stop();
            }
            return;
        }
        
        reconnectAttempts++;
        
        if (reconnectAttempts > MAX_RECONNECT_ATTEMPTS)
        {
            SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Stopping automatic reconnection after {MAX_RECONNECT_ATTEMPTS} attempts\r\n");
            
            if (reconnectionTimer != null && reconnectionTimer.Enabled)
            {
                reconnectionTimer.Stop();
            }
            return;
        }
        
        SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Attempting to reconnect to {reconnectPortName} (attempt {reconnectAttempts}/{MAX_RECONNECT_ATTEMPTS})...\r\n");
        
        try
        {
            // Close any existing connection
            CloseSerialPort();
            
            // Create new connection
            _serialPort = new SerialPort(reconnectPortName, reconnectBaudRate, reconnectParity, reconnectDataBits, reconnectStopBits);
            _serialPort.DataReceived += SerialPort_DataReceived;
            _serialPort.ErrorReceived += SerialPort_ErrorReceived;
            _serialPort.NewLine = "\r\n";
            _serialPort.ReadTimeout = 1000;
            _serialPort.WriteTimeout = 1000;
            
            // Try to open
            _serialPort.Open();
            
            if (_serialPort.IsOpen)
            {
                SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Successfully reconnected to {reconnectPortName}\r\n");
                
                if (reconnectionTimer != null && reconnectionTimer.Enabled)
                {
                    reconnectionTimer.Stop();
                }
                
                // Clear any stale buffer data
                try
                {
                    _serialPort.DiscardInBuffer();
                    _serialPort.DiscardOutBuffer();
                }
                catch (Exception) { /* Ignore buffer clear errors */ }
                
                // Clear our buffer too
                lock (_serialBufferLock)
                {
                    _serialDataBuffer = "";
                }
                
                // Reset heartbeat counter
                heartbeatMissedCount = 0;
            }
        }
        catch (Exception ex)
        {
            SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Reconnection attempt failed: {ex.Message}\r\n");
            
            // Will try again on next timer tick
        }
    }
    
    private void SerialPort_DataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        if (_isDisposing || _isShuttingDown) return; // Prevent processing during shutdown
        
        try
        {
            if (e.EventType == SerialData.Chars)
            {
                // Read all available data from the serial port buffer
                string receivedData = string.Empty;
                
                // Safely read data with retries and better error handling
                const int MAX_READ_RETRIES = 3;
                int readRetries = 0;
                Exception lastException = null;
                
                // Track if we've seen a read timeout - might indicate the device is slow
                bool hadTimeout = false;
                
                while (readRetries < MAX_READ_RETRIES && string.IsNullOrEmpty(receivedData))
                {
                    try 
                    {
                        if (_serialPort != null && _serialPort.IsOpen)
                        {
                            // Before reading, check how many bytes are available
                            int bytesToRead = _serialPort.BytesToRead;
                            
                            if (bytesToRead > 0)
                            {
                                // For large amounts of data, use a byte buffer instead of ReadExisting
                                // This can be more reliable with some serial devices
                                if (bytesToRead > 128)
                                {
                                    byte[] buffer = new byte[bytesToRead];
                                    int bytesRead = _serialPort.Read(buffer, 0, bytesToRead);
                                    
                                    if (bytesRead > 0)
                                    {
                                        // Convert bytes to string using ASCII encoding (common for scanners)
                                        receivedData = Encoding.ASCII.GetString(buffer, 0, bytesRead);
                                    }
                                }
                                else
                                {
                                    // For smaller amounts, ReadExisting is fine
                                    receivedData = _serialPort.ReadExisting();
                                }
                                
                                // If read returned empty but should have data, retry
                                if (string.IsNullOrEmpty(receivedData) && _serialPort.BytesToRead > 0)
                                {
                                    readRetries++;
                                    Thread.Sleep(10 * readRetries); // Increasing delay before retry
                                    continue;
                                }
                            }
                            else if (bytesToRead == 0)
                            {
                                // No data available despite event - could be a spurious event
                                // Just exit without error since there's nothing to read
                                return;
                            }
                            
                            break; // Successful read or confirmed no data, exit retry loop
                        }
                        else
                        {
                            // Port is unexpectedly closed
                            this.BeginInvoke(new Action(() => {
                                if (!_isDisposing && !_isShuttingDown)
                                {
                                    SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Serial port unexpectedly closed during data receive\r\n");
                                    AttemptSerialReconnection();
                                }
                            }));
                            return;
                        }
                    }
                    catch (TimeoutException)
                    {
                        // Special handling for timeouts - don't count as a retry, just wait longer
                        hadTimeout = true;
                        Thread.Sleep(50); // Wait longer for timeout
                        
                        // Log timeout only in debug mode to avoid spam
                        if (_debugMode)
                        {
                            this.BeginInvoke(new Action(() => {
                                if (!_isDisposing && !_isShuttingDown)
                                {
                                    SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Timeout waiting for serial data, retrying...\r\n");
                                }
                            }));
                        }
                        continue; // Try again without incrementing retry counter
                    }
                    catch (Exception ex)
                    {
                        lastException = ex;
                        readRetries++;
                        
                        if (readRetries < MAX_READ_RETRIES)
                        {
                            // Exponential backoff - increase delay with each retry
                            int delay = 20 * readRetries * readRetries;
                            Thread.Sleep(delay);
                        }
                        else
                        {
                            // Handle the exception after max retries
                            if (ex is IOException || ex is UnauthorizedAccessException)
                            {
                                // Handle I/O errors by attempting reconnection
                                this.BeginInvoke(new Action(() => {
                                    if (!_isDisposing && !_isShuttingDown)
                                    {
                                        SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Serial I/O error: {ex.Message}. Attempting reconnection...\r\n");
                                        AttemptSerialReconnection();
                                    }
                                }));
                                return;
                            }
                            else if (ex is InvalidOperationException)
                            {
                                // Port is likely closed - attempt to reopen
                                this.BeginInvoke(new Action(() => {
                                    if (!_isDisposing && !_isShuttingDown)
                                    {
                                        SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Serial port closed unexpectedly. Attempting reconnection...\r\n");
                                        AttemptSerialReconnection();
                                    }
                                }));
                                return;
                            }
                            else
                            {
                                // General error during reading
                                this.BeginInvoke(new Action(() => {
                                    if (!_isDisposing && !_isShuttingDown)
                                    {
                                        HandleSerialException(ex, "Serial read error");
                                        // For general errors, we'll still try to reconnect
                                        AttemptSerialReconnection();
                                    }
                                }));
                                return;
                            }
                        }
                    }
                }
                
                // If we got here with empty data after retries, handle the last exception
                if (string.IsNullOrEmpty(receivedData) && lastException != null)
                {
                    this.BeginInvoke(new Action(() => {
                        if (!_isDisposing && !_isShuttingDown)
                        {
                            HandleSerialException(lastException, "Failed to read serial data after retries");
                            AttemptSerialReconnection();
                        }
                    }));
                    return;
                }
                
                // Handle timeout without data
                if (string.IsNullOrEmpty(receivedData) && hadTimeout)
                {
                    // Just exit - no data after timeout
                    return;
                }
                
                if (string.IsNullOrEmpty(receivedData))
                    return;
                
                // Process data on UI thread
                this.BeginInvoke(new Action(() =>
                {
                    if (_isDisposing || _isShuttingDown) return;
                    
                    try
                    {
                        // Check if received data has any null bytes or other control characters
                        // Some scanners can send problematic data that needs to be filtered
                        bool hasInvalidChars = receivedData.Any(c => (c < 32 && c != '\r' && c != '\n') || c > 126);
                        
                        // Always show raw data in debug mode
                        if (_debugMode)
                        {
                            string hexData = BitConverter.ToString(Encoding.ASCII.GetBytes(receivedData)).Replace("-", " ");
                            SafeAppendText($"[{DateTime.Now:HH:mm:ss}] RAW HEX: {hexData}\r\n");
                            
                            string asciiDisplay = "";
                            foreach (char c in receivedData)
                            {
                                if (c < 32 || c > 126)
                                    asciiDisplay += $"<{(int)c}>";
                                else
                                    asciiDisplay += c;
                            }
                            SafeAppendText($"[{DateTime.Now:HH:mm:ss}] ASCII: {asciiDisplay}\r\n");
                            
                            if (hasInvalidChars)
                            {
                                SafeAppendText($"[{DateTime.Now:HH:mm:ss}] WARNING: Received data contains invalid characters\r\n");
                            }
                            
                            // Show buffer state
                            lock (_serialBufferLock)
                            {
                                SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Current buffer length: {_serialDataBuffer.Length}\r\n");
                            }
                        }
                        
                        // Filter out null bytes and other control characters that aren't terminators
                        if (hasInvalidChars)
                        {
                            receivedData = new string(receivedData.Where(c => c == '\r' || c == '\n' || (c >= 32 && c <= 126)).ToArray());
                            
                            if (_debugMode)
                            {
                                SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Data after filtering: {receivedData}\r\n");
                            }
                        }
                        
                        // Add data to buffer with thread safety
                        lock (_serialBufferLock)
                        {
                            _serialDataBuffer += receivedData;
                            
                            // Cap buffer size if it gets too large
                            const int ABSOLUTE_MAX_BUFFER = 2048;
                            if (_serialDataBuffer.Length > ABSOLUTE_MAX_BUFFER)
                            {
                                if (_debugMode)
                                {
                                    SafeAppendText($"[{DateTime.Now:HH:mm:ss}] WARNING: Buffer exceeded maximum size ({_serialDataBuffer.Length} bytes)\r\n");
                                }
                                
                                // Keep only the most recent data
                                _serialDataBuffer = _serialDataBuffer.Substring(_serialDataBuffer.Length - ABSOLUTE_MAX_BUFFER);
                                
                                if (_debugMode)
                                {
                                    SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Buffer truncated to {_serialDataBuffer.Length} bytes\r\n");
                                }
                            }
                        }
                        
                        // Check if buffer has a complete scan
                        ProcessSerialBuffer();
                    }
                    catch (Exception ex)
                    {
                        if (!_isDisposing)
                        {
                            HandleSerialException(ex, "Error processing serial data");
                        }
                    }
                }));
            }
        }
        catch (Exception ex)
        {
            // Must use BeginInvoke for UI operations from non-UI thread
            this.BeginInvoke(new Action(() =>
            {
                if (!_isDisposing)
                {
                    HandleSerialException(ex, "Error in serial data received handler");
                }
            }));
        }
    }

    // Add implementation for ProcessSerialBuffer method
    private void ProcessSerialBuffer(bool forceProcess = false)
    {
        if (_isDisposing) return;
        
        try
        {
            // Make a copy of the buffer for thread safety
            string dataToProcess;
            lock (_serialBufferLock)
            {
                dataToProcess = _serialDataBuffer;
            }
            
            if (string.IsNullOrEmpty(dataToProcess))
                return;
                
            // Look for complete scans in the buffer
            bool foundComplete = false;
            string processedData = "";
            
            // Most scanners terminate with CR, LF, or CRLF - we'll check for any
            if (dataToProcess.Contains("\r\n") || dataToProcess.Contains("\n") || dataToProcess.Contains("\r"))
            {
                // Handle multiple possible terminators by splitting on any
                string[] lines = dataToProcess.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
                
                // If we have complete lines, process them
                if (lines.Length > 1)
                {
                    // Process all lines except the last one (which might be incomplete)
                    for (int i = 0; i < lines.Length - 1; i++)
                    {
                        if (!string.IsNullOrWhiteSpace(lines[i]))
                        {
                            HandleScannedCode(lines[i]);
                            foundComplete = true;
                        }
                    }
                    
                    // Update the buffer to contain only the last incomplete line (if any)
                    processedData = lines[lines.Length - 1];
                    
                    // Only keep the last line if it's not empty
                    if (string.IsNullOrEmpty(processedData))
                    {
                        processedData = "";
                    }
                }
            }
            
            // If force processing due to timeout, handle any data in buffer
            if (!foundComplete && forceProcess && !string.IsNullOrEmpty(dataToProcess))
            {
                // No terminators found but timer expired - process what we have
                if (_debugMode)
                {
                    SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Processing unterminated data due to timeout\r\n");
                }
                
                HandleScannedCode(dataToProcess);
                processedData = "";
            }
            
            // Update the buffer with any unprocessed data
            lock (_serialBufferLock)
            {
                if (foundComplete || forceProcess)
                {
                    _serialDataBuffer = processedData;
                }
                
                // If we have data but didn't find terminators, start the timeout timer
                if (!foundComplete && !forceProcess && !string.IsNullOrEmpty(_serialDataBuffer))
                {
                    // Reset the timer for next check
                    if (_serialBufferTimer != null)
                    {
                        _serialBufferTimer.Stop();
                        _serialBufferTimer.Start();
                    }
                }
            }
            
            // Update last successful operation timestamp if we processed anything
            if (foundComplete || (forceProcess && !string.IsNullOrEmpty(dataToProcess)))
            {
                lastSuccessfulOperation = DateTime.Now;
            }
        }
        catch (Exception ex)
        {
            if (!_isDisposing)
            {
                HandleSerialException(ex, "Error processing serial buffer");
            }
        }
    }

    // Method to handle a complete scanned code
    private void HandleScannedCode(string code)
    {
        if (_isDisposing) return;
        
        try
        {
            // Clean the code (remove any trailing/leading whitespace)
            code = code.Trim();
            
            if (string.IsNullOrEmpty(code))
                return;
                
            // Display the scanned code
            SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Scanned: {code}\r\n");
            
            // Process the code based on its format
            ProcessScannedData(code);
        }
        catch (Exception ex)
        {
            if (!_isDisposing)
            {
                HandleSerialException(ex, "Error handling scanned code");
            }
        }
    }
    
    // Method to process the scanned data based on its format
    private void ProcessScannedData(string data)
    {
        if (_isDisposing) return;
        
        try
        {
            // Notify system of activity to prevent/exit idle mode
            NotifyActivity();
            
            // Check for specific formats or prefixes to determine the type of data
            if (data.StartsWith("JOB:", StringComparison.OrdinalIgnoreCase))
            {
                // This is a job code - extract and display job information
                string jobInfo = data.Substring(4).Trim();
                lastJobName = jobInfo;
                jobInfoBox.Text = jobInfo;
                SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Job set to: {jobInfo}\r\n");
            }
            else if (data.StartsWith("MAT:", StringComparison.OrdinalIgnoreCase))
            {
                // This is a material code - extract and display material information
                string matInfo = data.Substring(4).Trim();
                lastMaterial = matInfo;
                materialInfoBox.Text = matInfo;
                SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Material set to: {matInfo}\r\n");
            }
            else
            {
                // Generic code - handle as needed
                // This could be part information, operator ID, etc.
                SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Processing generic code: {data}\r\n");
                
                // Update any state variables as needed
                currentInput = data;
                
                // Optionally send to Mozaik or other system
                // SendToExternalSystem(data);
            }
        }
        catch (Exception ex)
        {
            if (!_isDisposing)
            {
                HandleSerialException(ex, "Error processing scanned data");
            }
        }
    }

    // New method to attempt reconnection when serial port fails
    private void AttemptSerialReconnection()
    {
        if (_isDisposing) return;
        
        try
        {
            SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Attempting serial port reconnection...\r\n");
            
            // Close the port if it's still open - with better error handling
            if (_serialPort != null)
            {
                try
                {
                    // First remove event handlers to prevent callback issues during reconnection
                    try
                    {
                        _serialPort.DataReceived -= SerialPort_DataReceived;
                        _serialPort.ErrorReceived -= SerialPort_ErrorReceived;
                    }
                    catch (Exception handlerEx) 
                    { 
                        Debug.WriteLine($"Error removing port handlers: {handlerEx.Message}"); 
                    }
                    
                    // Then try to close the port
                    try
                    {
                        if (_serialPort.IsOpen)
                        {
                            _serialPort.Close();
                            SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Closed previous serial port connection\r\n");
                        }
                    }
                    catch (Exception closeEx) 
                    {
                        Debug.WriteLine($"Error closing port: {closeEx.Message}");
                        // Continue with cleanup despite error
                    }
                    
                    // Finally dispose of the port
                    try
                    {
                        _serialPort.Dispose();
                    }
                    catch (Exception disposeEx) 
                    { 
                        Debug.WriteLine($"Error disposing port: {disposeEx.Message}"); 
                    }
                }
                catch (Exception ex) 
                { 
                    Debug.WriteLine($"Error during port cleanup: {ex.Message}");
                    /* Continue with reconnection despite errors */ 
                }
                finally
                {
                    // Ensure port reference is cleared even if exceptions occur
                    _serialPort = null;
                }
            }
            
            // Retrieve reconnection parameters
            string portToUse = string.IsNullOrEmpty(reconnectPortName) ? DEFAULT_COM_PORT : reconnectPortName;
            int baudToUse = reconnectBaudRate > 0 ? reconnectBaudRate : DEFAULT_BAUD_RATE;
            int dataBitsToUse = reconnectDataBits > 0 ? reconnectDataBits : 8;
            
            SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Reconnecting to {portToUse} at {baudToUse} baud\r\n");
            
            // Check port availability before attempting connection
            bool portAvailable = false;
            try
            {
                string[] availablePorts = SerialPort.GetPortNames();
                portAvailable = availablePorts.Any(p => p.Equals(portToUse, StringComparison.OrdinalIgnoreCase));
                
                if (!portAvailable)
                {
                    SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Warning: Port {portToUse} not found in available ports: {string.Join(", ", availablePorts)}\r\n");
                    
                    // If configured port isn't available but there are other ports, suggest first available
                    if (availablePorts.Length > 0)
                    {
                        SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Trying first available port: {availablePorts[0]}\r\n");
                        portToUse = availablePorts[0];
                        portAvailable = true;
                    }
                }
            }
            catch (Exception ex)
            {
                SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Error checking available ports: {ex.Message}\r\n");
                // Continue anyway, the port might still work
                portAvailable = true;
            }
            
            // Only try to connect if the port might be available
            if (portAvailable)
            {
                try
                {
                    // Create new instance with validated settings
                    _serialPort = new SerialPort(
                        portToUse, 
                        baudToUse,
                        reconnectParity,
                        dataBitsToUse,
                        reconnectStopBits);
                        
                    // Set appropriate timeouts (critical for reliability)
                    _serialPort.ReadTimeout = 1000;
                    _serialPort.WriteTimeout = 1000;
                    
                    // Explicitly configure other important settings
                    _serialPort.DtrEnable = true; // Data Terminal Ready
                    
                    // Add event handlers
                    _serialPort.DataReceived += SerialPort_DataReceived;
                    _serialPort.ErrorReceived += SerialPort_ErrorReceived;
                    
                    // Configure newline behavior
                    _serialPort.NewLine = "\r\n";
                    
                    // Try to open the port
                    _serialPort.Open();
                    
                    SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Successfully opened port {portToUse}\r\n");
                    
                    // Reset the heartbeat monitor
                    heartbeatMissedCount = 0;
                    
                    // Update connection state
                    lastSuccessfulOperation = DateTime.Now;
                    
                    // Stop any reconnection timer
                    if (reconnectionTimer != null && reconnectionTimer.Enabled)
                    {
                        reconnectionTimer.Stop();
                    }
                }
                catch (Exception ex)
                {
                    SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Failed to connect to {portToUse}: {ex.Message}\r\n");
                    
                    // Setup reconnection timer to try again
                    SetupReconnectionTimer(
                        portToUse, 
                        baudToUse,
                        reconnectParity,
                        dataBitsToUse,
                        reconnectStopBits);
                }
            }
            else
            {
                SafeAppendText($"[{DateTime.Now:HH:mm:ss}] No valid COM ports available, will try again later\r\n");
                
                // Setup timer to try again
                SetupReconnectionTimer(
                    portToUse, 
                    baudToUse,
                    reconnectParity,
                    dataBitsToUse,
                    reconnectStopBits);
            }
        }
        catch (Exception ex)
        {
            SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Error initializing serial port: {ex.Message}\r\n");
        }
    }

    // Improve reliability with async task-based operations for potentially blocking operations
    private async void AttemptSerialReconnectionAsync()
    {
        if (_isDisposing) return;
        
        try
        {
            SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Attempting serial port reconnection asynchronously...\r\n");
            
            // Run the potentially blocking operations on a background thread
            await Task.Run(() => 
            {
                try
                {
                    // Set higher thread priority for critical operation
                    Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
                    
                    // Close the port if it's still open - with better error handling
                    if (_serialPort != null)
                    {
                        try
                        {
                            // First remove event handlers to prevent callback issues during reconnection
                            try
                            {
                                _serialPort.DataReceived -= SerialPort_DataReceived;
                                _serialPort.ErrorReceived -= SerialPort_ErrorReceived;
                            }
                            catch (Exception handlerEx) 
                            { 
                                Debug.WriteLine($"Error removing port handlers: {handlerEx.Message}"); 
                            }
                            
                            // Then try to close the port
                            try
                            {
                                if (_serialPort.IsOpen)
                                {
                                    _serialPort.Close();
                                }
                            }
                            catch (Exception closeEx) 
                            {
                                Debug.WriteLine($"Error closing port: {closeEx.Message}");
                                // Continue with cleanup despite error
                            }
                            
                            // Finally dispose of the port
                            try
                            {
                                _serialPort.Dispose();
                            }
                            catch (Exception disposeEx) 
                            { 
                                Debug.WriteLine($"Error disposing port: {disposeEx.Message}"); 
                            }
                        }
                        catch (Exception ex) 
                        { 
                            Debug.WriteLine($"Error during port cleanup: {ex.Message}");
                            /* Continue with reconnection despite errors */ 
                        }
                        finally
                        {
                            // Ensure port reference is cleared even if exceptions occur
                            _serialPort = null;
                        }
                    }
                }
                finally
                {
                    // Restore thread priority when done
                    Thread.CurrentThread.Priority = ThreadPriority.Normal;
                }
            });
            
            // UI thread continued: safely update UI
            SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Closed previous serial port connection\r\n");
            
            // Retrieve reconnection parameters
            string portToUse = string.IsNullOrEmpty(reconnectPortName) ? DEFAULT_COM_PORT : reconnectPortName;
            int baudToUse = reconnectBaudRate > 0 ? reconnectBaudRate : DEFAULT_BAUD_RATE;
            int dataBitsToUse = reconnectDataBits > 0 ? reconnectDataBits : 8;
            
            SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Reconnecting to {portToUse} at {baudToUse} baud\r\n");
            
            // Check port availability before attempting connection - do this part asynchronously too
            bool portAvailable = false;
            try
            {
                string[] availablePorts = await Task.Run(() => SerialPort.GetPortNames());
                portAvailable = availablePorts.Any(p => p.Equals(portToUse, StringComparison.OrdinalIgnoreCase));
                
                if (!portAvailable)
                {
                    SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Warning: Port {portToUse} not found in available ports: {string.Join(", ", availablePorts)}\r\n");
                    
                    // If configured port isn't available but there are other ports, suggest first available
                    if (availablePorts.Length > 0)
                    {
                        SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Trying first available port: {availablePorts[0]}\r\n");
                        portToUse = availablePorts[0];
                        portAvailable = true;
                    }
                }
            }
            catch (Exception ex)
            {
                SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Error checking available ports: {ex.Message}\r\n");
                // Continue anyway, the port might still work
                portAvailable = true;
            }
            
            // Only try to connect if the port might be available
            if (portAvailable)
            {
                try
                {
                    // Create new instance with validated settings
                    _serialPort = new SerialPort(
                        portToUse, 
                        baudToUse,
                        reconnectParity,
                        dataBitsToUse,
                        reconnectStopBits);
                        
                    // Set appropriate timeouts (critical for reliability)
                    _serialPort.ReadTimeout = 1000;
                    _serialPort.WriteTimeout = 1000;
                    
                    // Explicitly configure other important settings
                    _serialPort.DtrEnable = true; // Data Terminal Ready
                    
                    // Add event handlers
                    _serialPort.DataReceived += SerialPort_DataReceived;
                    _serialPort.ErrorReceived += SerialPort_ErrorReceived;
                    
                    // Configure newline behavior
                    _serialPort.NewLine = "\r\n";
                    
                    // Try to open the port - do this in a timeout-protected way
                    var openSuccess = await Task.Run(() => 
                    {
                        try 
                        {
                            _serialPort.Open();
                            return true;
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Error opening port in async task: {ex.Message}");
                            return false;
                        }
                    });
                    
                    if (openSuccess)
                    {
                        SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Successfully opened port {portToUse}\r\n");
                        
                        // Reset the heartbeat monitor
                        heartbeatMissedCount = 0;
                        
                        // Update connection state
                        lastSuccessfulOperation = DateTime.Now;
                        
                        // Stop any reconnection timer
                        if (reconnectionTimer != null && reconnectionTimer.Enabled)
                        {
                            reconnectionTimer.Stop();
                        }
                    }
                    else
                    {
                        SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Failed to open port {portToUse}\r\n");
                        
                        // Setup reconnection timer to try again
                        SetupReconnectionTimer(
                            portToUse, 
                            baudToUse,
                            reconnectParity,
                            dataBitsToUse,
                            reconnectStopBits);
                    }
                }
                catch (Exception ex)
                {
                    SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Failed to connect to {portToUse}: {ex.Message}\r\n");
                    
                    // Setup reconnection timer to try again
                    SetupReconnectionTimer(
                        portToUse, 
                        baudToUse,
                        reconnectParity,
                        dataBitsToUse,
                        reconnectStopBits);
                }
            }
            else
            {
                SafeAppendText($"[{DateTime.Now:HH:mm:ss}] No valid COM ports available, will try again later\r\n");
                
                // Setup timer to try again
                SetupReconnectionTimer(
                    portToUse, 
                    baudToUse,
                    reconnectParity,
                    dataBitsToUse,
                    reconnectStopBits);
            }
        }
        catch (Exception ex)
        {
            SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Error initializing serial port: {ex.Message}\r\n");
        }
    }

    // Add compatibility method that calls the async version for existing callers
    private void AttemptSerialReconnection()
    {
        if (!_isDisposing)
        {
            AttemptSerialReconnectionAsync();
        }
    }

    // Use a thread-safe Invoke helper for UI updates
    private void InvokeIfRequired(Action action)
    {
        if (_isDisposing || _isShuttingDown) return;
        
        try
        {
            if (this.InvokeRequired)
            {
                // Use BeginInvoke instead of Invoke to prevent deadlocks
                this.BeginInvoke(action);
            }
            else
            {
                // Check if we're on UI thread but form is disposing
                if (this.IsDisposed || this.Disposing)
                    return;
                    
                action();
            }
        }
        catch (ObjectDisposedException)
        {
            // Form is already disposed, just ignore
        }
        catch (InvalidOperationException)
        {
            // Handle case where form handle doesn't exist
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"InvokeIfRequired error: {ex.Message}");
        }
    }

    // Add a ThreadSafe method for UI updates with the new helper
    private void SafeAppendText(string text)
    {
        if (_isDisposing || _isShuttingDown) return;
        
        try
        {
            InvokeIfRequired(() => {
                if (_isDisposing || _isShuttingDown || displayBox == null || displayBox.IsDisposed || displayBox.Disposing)
                    return;
                
                try
                {
                    // Check if the text buffer is getting too large
                    if (displayBox.TextLength > 100000)
                    {
                        // Keep only the last 50,000 characters
                        displayBox.Text = displayBox.Text.Substring(displayBox.TextLength - 50000);
                    }
                    
                    displayBox.AppendText(text);
                    
                    // Ensure text is visible by scrolling to end
                    displayBox.SelectionStart = displayBox.Text.Length;
                    displayBox.ScrollToCaret();
                    
                    // Force update to prevent UI freeze
                    Application.DoEvents();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error appending text: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error in SafeAppendText: {ex.Message}");
        }
    }

    // Add a method to handle serial port exceptions with more detail
    private void HandleSerialException(Exception ex, string context)
    {
        if (_isDisposing) return;
        
        string message = $"[{DateTime.Now:HH:mm:ss}] {context}: {ex.Message}";
        
        // Add more details for specific exception types
        if (ex is UnauthorizedAccessException)
        {
            message += " (Port may be in use by another application)";
        }
        else if (ex is IOException)
        {
            message += " (I/O error - device may be disconnected)";
        }
        else if (ex is InvalidOperationException)
        {
            message += " (Invalid operation - port may be closed)";
        }
        else if (ex is TimeoutException)
        {
            message += " (Operation timed out - device not responding)";
        }
        
        SafeAppendText(message + "\r\n");
        Debug.WriteLine(message);
        
        // Log stack trace in debug mode
        if (_debugMode)
        {
            Debug.WriteLine($"Stack trace: {ex.StackTrace}");
        }
        
        // Log the exception to a file for later analysis
        LogExceptionToFile(ex, context);
    }

    // Add a method to log exceptions to a file for better troubleshooting
    private void LogExceptionToFile(Exception ex, string context)
    {
        try
        {
            string logDirectory = Path.Combine(Application.StartupPath, "Logs");
            if (!Directory.Exists(logDirectory))
            {
                Directory.CreateDirectory(logDirectory);
            }
            
            string logFilePath = Path.Combine(logDirectory, $"ErrorLog_{DateTime.Now:yyyy-MM-dd}.txt");
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            
            // Build the error message with relevant details
            StringBuilder errorMsg = new StringBuilder();
            errorMsg.AppendLine($"[{timestamp}] CONTEXT: {context}");
            errorMsg.AppendLine($"EXCEPTION: {ex.GetType().FullName}");
            errorMsg.AppendLine($"MESSAGE: {ex.Message}");
            
            // Add inner exception details if present
            if (ex.InnerException != null)
            {
                errorMsg.AppendLine($"INNER EXCEPTION: {ex.InnerException.GetType().FullName}");
                errorMsg.AppendLine($"INNER MESSAGE: {ex.InnerException.Message}");
            }
            
            errorMsg.AppendLine($"STACK TRACE: {ex.StackTrace}");
            
            // Add serial port details if appropriate
            if (_serialPort != null)
            {
                errorMsg.AppendLine("SERIAL PORT STATE:");
                try
                {
                    errorMsg.AppendLine($"  Port Name: {_serialPort.PortName}");
                    errorMsg.AppendLine($"  Is Open: {_serialPort.IsOpen}");
                    errorMsg.AppendLine($"  Baud Rate: {_serialPort.BaudRate}");
                    errorMsg.AppendLine($"  Data Bits: {_serialPort.DataBits}");
                    errorMsg.AppendLine($"  Parity: {_serialPort.Parity}");
                    errorMsg.AppendLine($"  Stop Bits: {_serialPort.StopBits}");
                    errorMsg.AppendLine($"  Bytes To Read: {(_serialPort.IsOpen ? _serialPort.BytesToRead.ToString() : "N/A")}");
                }
                catch
                {
                    errorMsg.AppendLine("  (Error retrieving serial port details)");
                }
            }
            
            errorMsg.AppendLine(new string('-', 80)); // Separator
            
            // Append to log file with lock to prevent multiple threads writing simultaneously
            lock (_fileLock)
            {
                File.AppendAllText(logFilePath, errorMsg.ToString());
            }
        }
        catch (Exception logEx)
        {
            // Don't throw from error handler - just output to debug
            Debug.WriteLine($"Error writing to log file: {logEx.Message}");
        }
    }

    // Add a watchdog timer for application health monitoring
    private System.Windows.Forms.Timer watchdogTimer;
    private DateTime lastSuccessfulOperation = DateTime.Now;
    private int watchdogResetCount = 0;
    private const int MAX_WATCHDOG_RESETS = 5;
    private const int WATCHDOG_RESET_INTERVAL_MINUTES = 60; // Reset counter after this many minutes of stability
    private DateTime lastWatchdogCounterReset = DateTime.Now;

    private void InitializeWatchdog()
    {
        if (_isDisposing) return;
        
        try
        {
            if (watchdogTimer != null)
            {
                watchdogTimer.Stop();
                watchdogTimer.Dispose();
            }
            
            watchdogTimer = new System.Windows.Forms.Timer();
            watchdogTimer.Interval = 60000; // Check every minute
            watchdogTimer.Tick += WatchdogTimer_Tick;
            
            // Track this timer
            _timers.Add(watchdogTimer);
            
            watchdogTimer.Start();
            
            if (_debugMode)
            {
                SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Watchdog monitoring started (60s interval)\r\n");
            }
            
            // Initialize the last successful operation time
            lastSuccessfulOperation = DateTime.Now;
        }
        catch (Exception ex)
        {
            if (!_isDisposing)
            {
                SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Error initializing watchdog: {ex.Message}\r\n");
            }
            Debug.WriteLine($"Error in InitializeWatchdog: {ex.Message}");
        }
    }

    private void WatchdogTimer_Tick(object sender, EventArgs e)
    {
        if (_isDisposing) return;
        
        try
        {
            // Check if application is responsive
            TimeSpan timeSinceLastSuccess = DateTime.Now - lastSuccessfulOperation;
            
            // Reset watchdog counter if sufficient stable time has passed
            if (watchdogResetCount > 0 && timeSinceLastSuccess.TotalMinutes < 5 && 
                (DateTime.Now - lastWatchdogCounterReset).TotalMinutes >= WATCHDOG_RESET_INTERVAL_MINUTES)
            {
                if (_debugMode)
                {
                    SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Watchdog: System stable for {WATCHDOG_RESET_INTERVAL_MINUTES} minutes, resetting counter from {watchdogResetCount} to 0\r\n");
                }
                watchdogResetCount = 0;
                lastWatchdogCounterReset = DateTime.Now;
            }
            
            // Check for excessive time without successful operation
            if (timeSinceLastSuccess.TotalMinutes > 15) // No successful operations for 15+ minutes
            {
                watchdogResetCount++;
                SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Watchdog: No activity for {timeSinceLastSuccess.TotalMinutes:0.0} minutes. Performing system reset ({watchdogResetCount}/{MAX_WATCHDOG_RESETS})\r\n");
                
                // Perform different levels of recovery based on count
                if (watchdogResetCount <= 2)
                {
                    // Level 1: Just try to reconnect serial port
                    SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Watchdog: Level 1 reset - reconnecting serial port\r\n");
                    AttemptSerialReconnection();
                }
                else if (watchdogResetCount <= 4)
                {
                    // Level 2: Full reset of serial subsystem
                    SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Watchdog: Level 2 reset - full serial subsystem restart\r\n");
                    
                    // Close current port
                    CloseSerialPort();
                    
                    // Clean up and recreate all buffers
                    lock (_serialBufferLock)
                    {
                        _serialDataBuffer = "";
                    }
                    
                    // Reset missed heartbeat count
                    heartbeatMissedCount = 0;
                    
                    // Stop and restart heartbeat timer
                    if (heartbeatTimer != null)
                    {
                        heartbeatTimer.Stop();
                        heartbeatTimer.Start();
                    }
                    
                    // Delay before reconnection attempt
                    Thread.Sleep(500);
                    
                    // Try to reconnect
                    InitializeSerialPort();
                }
                else if (watchdogResetCount >= MAX_WATCHDOG_RESETS)
                {
                    // Level 3: Last resort - offer to restart application
                    SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Watchdog: Level 3 reset - critical failure detected\r\n");
                    
                    // Ask user if they want to restart
                    var result = MessageBox.Show(
                        "The scanner system has experienced multiple failures and may need to be restarted.\n\n" +
                        "Would you like to restart the application now?",
                        "QR Code Scanner - Critical Error",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning
                    );
                    
                    if (result == DialogResult.Yes)
                    {
                        // Restart the application
                        SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Watchdog: Restarting application\r\n");
                        
                        try
                        {
                            // Start a new instance before closing this one
                            Process.Start(Application.ExecutablePath);
                            
                            // Set flag and shut down
                            _isDisposing = true;
                            Application.Exit();
                        }
                        catch (Exception ex)
                        {
                            SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Error during restart: {ex.Message}\r\n");
                            watchdogResetCount = MAX_WATCHDOG_RESETS - 1; // Allow one more try
                        }
                    }
                    else
                    {
                        // User chose not to restart - reset counter but set a lower threshold
                        SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Watchdog: User declined restart, reducing threshold\r\n");
                        watchdogResetCount = MAX_WATCHDOG_RESETS - 2;
                    }
                }
                
                // Reset timer for next check
                lastSuccessfulOperation = DateTime.Now;
            }
            
            // Perform light system checks even when things are working
            PerformSystemHealthCheck();
        }
        catch (Exception ex)
        {
            if (!_isDisposing)
            {
                SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Error in watchdog timer: {ex.Message}\r\n");
            }
        }
    }

    private System.Windows.Forms.Timer heartbeatTimer;
    private int heartbeatMissedCount = 0;
    private const int MAX_MISSED_HEARTBEATS = 3;

    private void InitializeHeartbeatMonitor()
    {
        if (_isDisposing) return;
        
        try
        {
            heartbeatTimer = new System.Windows.Forms.Timer();
            heartbeatTimer.Interval = 5000; // Check every 5 seconds
            heartbeatTimer.Tick += HeartbeatTimer_Tick;
            _timers.Add(heartbeatTimer);
            heartbeatTimer.Start();
            
            if (_debugMode)
            {
                SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Heartbeat monitoring started\r\n");
            }
        }
        catch (Exception ex)
        {
            HandleSerialException(ex, "Error initializing heartbeat monitor");
        }
    }

    private void HeartbeatTimer_Tick(object sender, EventArgs e)
    {
        if (_isDisposing) return;
        
        try
        {
            if (_serialPort != null && _serialPort.IsOpen)
            {
                try
                {
                    // Send a simple heartbeat command
                    _serialPort.Write(new byte[] { 0x00 }, 0, 1);
                    heartbeatMissedCount = 0; // Reset on successful send
                }
                catch (Exception)
                {
                    heartbeatMissedCount++;
                    
                    if (heartbeatMissedCount >= MAX_MISSED_HEARTBEATS)
                    {
                        SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Heartbeat check failed {heartbeatMissedCount} times\r\n");
                        AttemptSerialReconnection();
                        heartbeatMissedCount = 0;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            if (!_isDisposing)
            {
                HandleSerialException(ex, "Error in heartbeat check");
            }
        }
    }

    public Form1()
    {
        try
        {
            // Set up form and thread exception handling first
            Application.ThreadException += Application_ThreadException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            
            // Initialize component (designer-generated code)
            InitializeComponent();
            
            // Initialize core UI elements
            InitializeDisplay();
            InitializeTrayIcon();
            
            // Indicate startup is occurring
            SafeAppendText($"[{DateTime.Now:HH:mm:ss}] QR Code Scanner Starting...\r\n");
            
            // Create serial buffer timer
            _serialBufferTimer = new System.Windows.Forms.Timer();
            _serialBufferTimer.Interval = 300; // 300ms timeout for partial data
            _serialBufferTimer.Tick += SerialBufferTimer_Tick;
            _timers.Add(_serialBufferTimer);
            
            // Initialize other important event handlers
            this.FormClosing += Form1_FormClosing;
            this.Resize += Form1_Resize;
            this.Activated += Form1_Activated; // For detecting user activity
            
            // Set form properties
            this.Text = "QR Code Scanner";
            this.Size = new Size(600, 400);
            
            // Verify settings files integrity before loading
            VerifySettingsIntegrity();
            
            // Initialize scanner state
            ResetScannerState();
            
            // Initialize health monitoring systems
            InitializeWatchdog();
            InitializeHeartbeatMonitor();
            
            // Initialize system power event handling
            InitializeSystemEvents();
            
            // Initialize the serial port (after monitoring systems are ready)
            InitializeSerialPort();
            
            // Check command line arguments for auto-start minimized
            string[] args = Environment.GetCommandLineArgs();
            if (args.Length > 1 && args[1].Equals("/minimized", StringComparison.OrdinalIgnoreCase))
            {
                this.WindowState = FormWindowState.Minimized;
                this.ShowInTaskbar = false;
                this.Hide();
                
                // Activate idle mode for background operation
                EnterIdleMode();
            }
            
            // Add "Start with Windows" option in the tray context menu
            AddStartupOption();
            
            // Verify hardcoded directories exist
            VerifyMozaikDirectories();
            
            // Clean up old logs on startup
            CleanupOldLogFiles();
            
            // Schedule periodic log cleanup
            var logCleanupTimer = new System.Windows.Forms.Timer();
            logCleanupTimer.Interval = 24 * 60 * 60 * 1000; // Once per day
            logCleanupTimer.Tick += (s, e) => CleanupOldLogFiles();
            _timers.Add(logCleanupTimer);
            logCleanupTimer.Start();
            
            // Initial notification of activity
            NotifyActivity();
            
            // Prevent system sleep while scanner is running
            SetThreadExecutionState(EXECUTION_STATE.ES_CONTINUOUS | EXECUTION_STATE.ES_SYSTEM_REQUIRED);
            
            // Perform startup health check
            PerformStartupHealthCheck();
            
            // Show ready status
            SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Scanner initialized and ready\r\n");
        }
        catch (Exception ex)
        {
            // Critical startup error - log and display
            try
            {
                MessageBox.Show(
                    $"Critical error during application startup: {ex.Message}\r\n\r\n" +
                    $"Error details have been written to the application log.",
                    "QR Code Scanner - Startup Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                
                // Log the exception
                LogExceptionToFile(ex, "CRITICAL STARTUP ERROR");
                
                // Try to write to debug output even if UI isn't ready
                Debug.WriteLine($"CRITICAL STARTUP ERROR: {ex.Message}");
                Debug.WriteLine(ex.StackTrace);
            }
            catch
            {
                // Last resort if even the error handling fails
                MessageBox.Show(
                    "Fatal error during application startup. The application will now exit.",
                    "QR Code Scanner",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            
            // Exit the application
            _isDisposing = true;
            _isShuttingDown = true;
            Application.Exit();
        }
    }
    
    // Add global exception handlers
    private void Application_ThreadException(object sender, ThreadExceptionEventArgs e)
    {
        try
        {
            // Log the exception
            LogExceptionToFile(e.Exception, "UNHANDLED THREAD EXCEPTION");
            
            if (_debugMode)
            {
                SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Unhandled thread exception: {e.Exception.Message}\r\n");
            }
            
            // Let watchdog handle recovery based on severity
            lastSuccessfulOperation = DateTime.Now.AddMinutes(-16); // Trigger watchdog recovery
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error in thread exception handler: {ex.Message}");
        }
    }
    
    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        try
        {
            Exception ex = e.ExceptionObject as Exception;
            string message = ex != null ? ex.Message : "Unknown error";
            bool isTerminating = e.IsTerminating;
            
            // Log the exception
            if (ex != null)
            {
                LogExceptionToFile(ex, "UNHANDLED DOMAIN EXCEPTION");
            }
            else
            {
                try
                {
                    File.AppendAllText(
                        Path.Combine(Application.StartupPath, "Logs", $"CriticalError_{DateTime.Now:yyyy-MM-dd}.txt"),
                        $"[{DateTime.Now}] CRITICAL ERROR: {e.ExceptionObject?.ToString() ?? "Unknown"}\r\n");
                }
                catch
                {
                    // Ignore logging errors at this critical stage
                }
            }
            
            // If this is terminating, try to show a message
            if (isTerminating)
            {
                try
                {
                    MessageBox.Show(
                        $"Critical error: {message}\r\n\r\nThe application needs to close.",
                        "QR Code Scanner - Critical Error",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
                catch
                {
                    // Ignore UI errors at this critical stage
                }
            }
            else if (_debugMode)
            {
                SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Unhandled domain exception: {message}\r\n");
            }
        }
        catch
        {
            // Ignore errors in the error handler
        }
    }
    
    // Add startup health check
    private void PerformStartupHealthCheck()
    {
        try
        {
            // Check available memory
            long availableMemoryMB = GetAvailableMemory() / (1024 * 1024);
            
            if (availableMemoryMB < 200)
            {
                SafeAppendText($"[{DateTime.Now:HH:mm:ss}] WARNING: Low system memory ({availableMemoryMB}MB available). " +
                    "This may affect application performance.\r\n");
            }
            
            // Check if any COM ports are available
            string[] availablePorts = SerialPort.GetPortNames();
            if (availablePorts.Length == 0)
            {
                SafeAppendText($"[{DateTime.Now:HH:mm:ss}] WARNING: No COM ports detected. " +
                    "Scanner functionality may be unavailable.\r\n");
            }
            
            // Check for read/write access to application directory
            string testFilePath = Path.Combine(Application.StartupPath, "write_test.tmp");
            try
            {
                File.WriteAllText(testFilePath, "test");
                File.Delete(testFilePath);
            }
            catch (Exception ex)
            {
                SafeAppendText($"[{DateTime.Now:HH:mm:ss}] WARNING: No write access to application directory: {ex.Message}\r\n");
            }
            
            // Check if process has admin rights (might be needed for some COM port access)
            bool isAdmin = IsAdministrator();
            if (!isAdmin)
            {
                SafeAppendText($"[{DateTime.Now:HH:mm:ss}] NOTE: Running without administrator privileges. " +
                    "This may limit access to some system resources.\r\n");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error in startup health check: {ex.Message}");
        }
    }
    
    // Get available system memory
    private long GetAvailableMemory()
    {
        try
        {
            return new Microsoft.VisualBasic.Devices.ComputerInfo().AvailablePhysicalMemory;
        }
        catch
        {
            // Fallback if Computer Info not available
            return GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        }
    }
    
    // Check if running as administrator
    private bool IsAdministrator()
    {
        try
        {
            using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
            {
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
        }
        catch
        {
            return false;
        }
    }

    private void InitializeDisplay()
    {
        // Create info panel
        var infoPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 100,
            Padding = new Padding(5)
        };

        // Create labels
        var jobLabel = new Label { Text = "Job Information:", Dock = DockStyle.Left, Width = 100 };
        var materialLabel = new Label { Text = "Material:", Dock = DockStyle.Left, Width = 100 };

        // Create job info box
        jobInfoBox = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            Height = 40,
            Dock = DockStyle.Top,
            Font = new Font("Consolas", 10F, FontStyle.Regular),
            BackColor = Color.LightYellow
        };

        // Create material info box
        materialInfoBox = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            Height = 40,
            Dock = DockStyle.Top,
            Font = new Font("Consolas", 10F, FontStyle.Regular),
            BackColor = Color.LightGreen
        };

        // Create panels for each row
        var jobPanel = new Panel { Height = 45, Dock = DockStyle.Top, Padding = new Padding(5) };
        var materialPanel = new Panel { Height = 45, Dock = DockStyle.Top, Padding = new Padding(5) };

        jobPanel.Controls.Add(jobInfoBox);
        jobPanel.Controls.Add(jobLabel);
        materialPanel.Controls.Add(materialInfoBox);
        materialPanel.Controls.Add(materialLabel);

        infoPanel.Controls.Add(jobPanel);
        infoPanel.Controls.Add(materialPanel);

        // Add test button
        var testButton = new Button
        {
            Text = "Test Scanner",
            Dock = DockStyle.Bottom,
            Height = 30
        };
        testButton.Click += TestButton_Click;
        
        // Add COM port setup button
        var comSetupButton = new Button
        {
            Text = "Configure COM Port",
            Dock = DockStyle.Bottom,
            Height = 30
        };
        comSetupButton.Click += ComSetupButton_Click;
        
        // Create display box for scanned codes history
        displayBox = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            Font = new Font("Consolas", 10F, FontStyle.Regular)
        };
        
        // Add timestamp to entries
        displayBox.Text = $"QR Code Scanner Started: {DateTime.Now}\r\n";

        // Add controls to form
        this.Controls.Add(displayBox);
        this.Controls.Add(testButton);
        this.Controls.Add(comSetupButton);  // Add COM port setup button
        this.Controls.Add(infoPanel);
    }

    private void BackupSettings()
    {
        if (_isDisposing) return;
        
        try
        {
            string backupPath = Path.Combine(Application.StartupPath, "settings.backup");
            string settingsPath = Path.Combine(Application.StartupPath, SETTINGS_FILE);
            
            if (File.Exists(settingsPath))
            {
                File.Copy(settingsPath, backupPath, true);
                if (_debugMode)
                {
                    SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Settings backup created\r\n");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error backing up settings: {ex.Message}");
        }
    }
    
    private void RestoreSettingsFromBackup()
    {
        if (_isDisposing) return;
        
        try
        {
            string backupPath = Path.Combine(Application.StartupPath, "settings.backup");
            string settingsPath = Path.Combine(Application.StartupPath, SETTINGS_FILE);
            
            if (!File.Exists(settingsPath) && File.Exists(backupPath))
            {
                File.Copy(backupPath, settingsPath, true);
                SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Settings restored from backup\r\n");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error restoring settings: {ex.Message}");
        }
    }
    
    private void VerifySettingsIntegrity()
    {
        if (_isDisposing) return;
        
        try
        {
            string settingsPath = Path.Combine(Application.StartupPath, SETTINGS_FILE);
            
            if (File.Exists(settingsPath))
            {
                // Try to read and parse settings to verify integrity
                var lines = File.ReadAllLines(settingsPath);
                bool settingsValid = lines.Any() && lines.All(l => l.Contains("="));
                
                if (!settingsValid)
                {
                    SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Settings file corrupted, attempting restore\r\n");
                    RestoreSettingsFromBackup();
                }
                else
                {
                    // Create backup of valid settings
                    BackupSettings();
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error verifying settings: {ex.Message}");
            RestoreSettingsFromBackup();
        }
    }

    private void CleanupOldLogFiles()
    {
        if (_isDisposing) return;
        
        try
        {
            string logDirectory = Path.Combine(Application.StartupPath, "Logs");
            if (!Directory.Exists(logDirectory))
                return;
                
            // Get all log files
            var logFiles = Directory.GetFiles(logDirectory, "ErrorLog_*.txt")
                                  .Select(f => new FileInfo(f))
                                  .OrderByDescending(f => f.LastWriteTime)
                                  .ToList();
            
            // Keep last 7 days of logs
            var cutoffDate = DateTime.Now.AddDays(-7);
            var oldLogs = logFiles.Where(f => f.LastWriteTime < cutoffDate).ToList();
            
            // Delete old logs
            foreach (var log in oldLogs)
            {
                try
                {
                    log.Delete();
                    if (_debugMode)
                    {
                        SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Cleaned up old log file: {log.Name}\r\n");
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error deleting old log file {log.Name}: {ex.Message}");
                }
            }
            
            // Check total size of remaining logs
            long totalSize = logFiles.Where(f => f.Exists).Sum(f => f.Length);
            long maxSize = 100L * 1024 * 1024; // 100MB max
            
            if (totalSize > maxSize)
            {
                SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Log directory exceeds size limit, performing cleanup\r\n");
                
                // Remove oldest logs until under size limit
                var remainingLogs = logFiles.Where(f => f.Exists)
                                          .OrderByDescending(f => f.LastWriteTime)
                                          .ToList();
                
                for (int i = remainingLogs.Count - 1; i >= 0; i--)
                {
                    if (totalSize <= maxSize)
                        break;
                        
                    try
                    {
                        totalSize -= remainingLogs[i].Length;
                        remainingLogs[i].Delete();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error during size-based cleanup of {remainingLogs[i].Name}: {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error in log cleanup: {ex.Message}");
        }
    }
    
    // Add implementation for ResetScannerState method
    private void ResetScannerState()
    {
        if (_isDisposing) return;
        
        try
        {
            // Clear all state variables
            currentInput = "";
            lastJobName = null;
            lastMaterial = null;
            
            // Reset UI elements
            jobInfoBox.Text = "";
            materialInfoBox.Text = "";
            
            // Reset buffers
            lock (_serialBufferLock)
            {
                _serialDataBuffer = "";
            }
            _scanBuffer.Clear();
            
            // Reset heartbeat and watchdog counters
            heartbeatMissedCount = 0;
            watchdogResetCount = 0;
            lastSuccessfulOperation = DateTime.Now;
            lastWatchdogCounterReset = DateTime.Now;
            
            SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Scanner state reset\r\n");
        }
        catch (Exception ex)
        {
            if (!_isDisposing)
            {
                HandleSerialException(ex, "Error resetting scanner state");
            }
        }
    }
    
    // Add implementation for InitializeSerialPort method
    private void InitializeSerialPort()
    {
        if (_isDisposing) return;
        
        try
        {
            // Close existing port if open
            CloseSerialPort();
            
            // Load settings from config file
            string portName = DEFAULT_COM_PORT;
            int baudRate = DEFAULT_BAUD_RATE;
            Parity parity = Parity.None;
            int dataBits = 8;
            StopBits stopBits = StopBits.One;
            
            try
            {
                string settingsPath = Path.Combine(Application.StartupPath, SETTINGS_FILE);
                if (File.Exists(settingsPath))
                {
                    var lines = File.ReadAllLines(settingsPath);
                    
                    foreach (string line in lines)
                    {
                        if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#"))
                            continue;
                            
                        string[] parts = line.Split('=');
                        if (parts.Length != 2)
                            continue;
                            
                        string key = parts[0].Trim();
                        string value = parts[1].Trim();
                        
                        switch (key.ToLower())
                        {
                            case "port":
                                portName = value;
                                break;
                            case "baudrate":
                                if (int.TryParse(value, out int baud))
                                    baudRate = baud;
                                break;
                            case "parity":
                                if (Enum.TryParse(value, true, out Parity p))
                                    parity = p;
                                break;
                            case "databits":
                                if (int.TryParse(value, out int bits))
                                    dataBits = bits;
                                break;
                            case "stopbits":
                                if (Enum.TryParse(value, true, out StopBits sb))
                                    stopBits = sb;
                                break;
                            case "debug":
                                if (bool.TryParse(value, out bool debug))
                                    _debugMode = debug;
                                break;
                        }
                    }
                    
                    SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Loaded settings from {SETTINGS_FILE}\r\n");
                }
                else
                {
                    // Create default settings file
                    CreateDefaultSettingsFile();
                }
            }
            catch (Exception ex)
            {
                SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Error loading settings: {ex.Message}\r\n");
                // Continue with defaults
            }
            
            // Debug mode announcement
            if (_debugMode)
            {
                SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Debug mode enabled\r\n");
            }
            
            // Check if serial port name is valid
            bool portAvailable = false;
            try
            {
                string[] availablePorts = SerialPort.GetPortNames();
                portAvailable = availablePorts.Any(p => p.Equals(portName, StringComparison.OrdinalIgnoreCase));
                
                if (!portAvailable)
                {
                    SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Warning: Port {portName} not found in available ports: {string.Join(", ", availablePorts)}\r\n");
                    
                    // If configured port isn't available but there are other ports, use first available
                    if (availablePorts.Length > 0)
                    {
                        SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Using first available port: {availablePorts[0]}\r\n");
                        portName = availablePorts[0];
                        portAvailable = true;
                    }
                }
            }
            catch (Exception ex)
            {
                SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Error checking available ports: {ex.Message}\r\n");
                // Continue anyway, the port might still work
                portAvailable = true;
            }
            
            // Only try to connect if a port might be available
            if (portAvailable)
            {
                try
                {
                    // Create and configure the serial port
                    _serialPort = new SerialPort(portName, baudRate, parity, dataBits, stopBits);
                    
                    // Set appropriate timeouts
                    _serialPort.ReadTimeout = 1000;
                    _serialPort.WriteTimeout = 1000;
                    
                    // Configure hardware flow control for reliability
                    _serialPort.DtrEnable = true; // Data Terminal Ready
                    
                    // Set event handlers
                    _serialPort.DataReceived += SerialPort_DataReceived;
                    _serialPort.ErrorReceived += SerialPort_ErrorReceived;
                    
                    // Configure line termination
                    _serialPort.NewLine = "\r\n";
                    
                    // Open the port
                    _serialPort.Open();
                    
                    // Store reconnection parameters for future use
                    reconnectPortName = portName;
                    reconnectBaudRate = baudRate;
                    reconnectParity = parity;
                    reconnectDataBits = dataBits;
                    reconnectStopBits = stopBits;
                    
                    SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Connected to {portName} at {baudRate} baud\r\n");
                    
                    // Update status
                    lastSuccessfulOperation = DateTime.Now;
                }
                catch (Exception ex)
                {
                    SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Failed to open {portName}: {ex.Message}\r\n");
                    
                    // Setup reconnection timer to try again
                    SetupReconnectionTimer(portName, baudRate, parity, dataBits, stopBits);
                }
            }
            else
            {
                SafeAppendText($"[{DateTime.Now:HH:mm:ss}] No COM ports available, will try again later\r\n");
                
                // Setup autodetection/reconnection timer for when ports become available
                SetupReconnectionTimer(portName, baudRate, parity, dataBits, stopBits);
            }
        }
        catch (Exception ex)
        {
            SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Error initializing serial port: {ex.Message}\r\n");
        }
    }
    
    // Method to create default settings file
    private void CreateDefaultSettingsFile()
    {
        try
        {
            string settingsPath = Path.Combine(Application.StartupPath, SETTINGS_FILE);
            
            // Create default settings
            List<string> defaultSettings = new List<string>
            {
                "# QR Code Scanner Settings",
                "# Format: key=value",
                "",
                $"port={DEFAULT_COM_PORT}",
                $"baudrate={DEFAULT_BAUD_RATE}",
                "parity=None",
                "databits=8",
                "stopbits=One",
                "debug=false"
            };
            
            // Write to file
            File.WriteAllLines(settingsPath, defaultSettings);
            
            SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Created default settings file\r\n");
        }
        catch (Exception ex)
        {
            SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Error creating default settings: {ex.Message}\r\n");
        }
    }
    
    // Method to safely close the serial port
    private void CloseSerialPort()
    {
        try
        {
            if (_serialPort != null)
            {
                // Remove event handlers first to prevent callback issues
                try
                {
                    _serialPort.DataReceived -= SerialPort_DataReceived;
                    _serialPort.ErrorReceived -= SerialPort_ErrorReceived;
                }
                catch (Exception ex) { Debug.WriteLine($"Error removing handlers: {ex.Message}"); }
                
                // Close the port if open
                try
                {
                    if (_serialPort.IsOpen)
                    {
                        _serialPort.DiscardInBuffer();
                        _serialPort.DiscardOutBuffer();
                        _serialPort.Close();
                    }
                }
                catch (Exception ex) { Debug.WriteLine($"Error closing port: {ex.Message}"); }
                
                // Dispose of the port
                try
                {
                    _serialPort.Dispose();
                }
                catch (Exception ex) { Debug.WriteLine($"Error disposing port: {ex.Message}"); }
                
                _serialPort = null;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error in CloseSerialPort: {ex.Message}");
        }
    }
    
    // Method to verify Mozaik directories exist
    private void VerifyMozaikDirectories()
    {
        try
        {
            // Check if Mozaik settings folder exists
            if (!Directory.Exists(MOZAIK_SETTINGS_FOLDER))
            {
                SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Warning: Mozaik settings folder not found at {MOZAIK_SETTINGS_FOLDER}\r\n");
            }
            
            // Check if State.dat file exists
            if (!File.Exists(MOZAIK_STATE_DAT_PATH))
            {
                SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Warning: Mozaik state file not found at {MOZAIK_STATE_DAT_PATH}\r\n");
            }
            
            // Check if optimize.exe exists
            if (!File.Exists(OPTIMIZE_EXE_PATH))
            {
                SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Warning: Mozaik optimize executable not found at {OPTIMIZE_EXE_PATH}\r\n");
            }
        }
        catch (Exception ex)
        {
            SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Error verifying Mozaik directories: {ex.Message}\r\n");
        }
    }
    
    // Add Form1_FormClosing event handler
    private void Form1_FormClosing(object sender, FormClosingEventArgs e)
    {
        // Set flag to prevent processing during shutdown
        _isDisposing = true;
        _isShuttingDown = true;
        
        SafeAppendText("Shutting down scanner application...\r\n");
        
        try
        {
            // Unregister from system events first
            CleanupSystemEvents();
            
            // Rest of the existing cleanup code
            // Stop all timers
            foreach (var timer in _timers)
            {
                try
                {
                    if (timer != null && timer.Enabled)
                    {
                        timer.Stop();
                    }
                    
                    if (timer != null)
                    {
                        timer.Tick -= null; // Remove all event handlers
                        timer.Dispose();
                    }
                }
                catch (Exception timerEx) 
                { 
                    Debug.WriteLine($"Error disposing timer: {timerEx.Message}"); 
                }
            }
            
            // Clear timers list
            _timers.Clear();
            
            // Stop individual timers that might not be in the list
            if (reconnectionTimer != null)
            {
                try
                {
                    reconnectionTimer.Stop();
                    reconnectionTimer.Tick -= ReconnectionTimer_Tick;
                    reconnectionTimer.Dispose();
                    reconnectionTimer = null;
                }
                catch { /* Ignore errors during shutdown */ }
            }
            
            if (watchdogTimer != null)
            {
                try
                {
                    watchdogTimer.Stop();
                    watchdogTimer.Tick -= WatchdogTimer_Tick;
                    watchdogTimer.Dispose();
                    watchdogTimer = null;
                }
                catch { /* Ignore errors during shutdown */ }
            }
            
            if (heartbeatTimer != null)
            {
                try
                {
                    heartbeatTimer.Stop();
                    heartbeatTimer.Tick -= HeartbeatTimer_Tick;
                    heartbeatTimer.Dispose();
                    heartbeatTimer = null;
                }
                catch { /* Ignore errors during shutdown */ }
            }
            
            if (_serialBufferTimer != null)
            {
                try
                {
                    _serialBufferTimer.Stop();
                    _serialBufferTimer.Tick -= SerialBufferTimer_Tick;
                    _serialBufferTimer.Dispose();
                    _serialBufferTimer = null;
                }
                catch { /* Ignore errors during shutdown */ }
            }
            
            if (periodicCaseCheckTimer != null)
            {
                try
                {
                    periodicCaseCheckTimer.Stop();
                    periodicCaseCheckTimer.Dispose();
                    periodicCaseCheckTimer = null;
                }
                catch { /* Ignore errors during shutdown */ }
            }
            
            // Close and clean up serial port
            CloseSerialPort();
            
            // Dispose any file system watchers
            if (casePreservationWatcher != null)
            {
                try
                {
                    casePreservationWatcher.EnableRaisingEvents = false;
                    casePreservationWatcher.Dispose();
                    casePreservationWatcher = null;
                }
                catch { /* Ignore errors during shutdown */ }
            }
            
            // Dispose tray icon
            if (trayIcon != null)
            {
                trayIcon.Visible = false;
                trayIcon.Dispose();
                trayIcon = null;
            }
            
            // Clear all buffers
            _serialDataBuffer = "";
            _scanBuffer.Clear();
            
            // Force a garbage collection before exit
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            
            SafeAppendText("Shutdown complete.\r\n");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error during shutdown: {ex.Message}");
        }
    }
    
    // Add Form1_Resize event handler
    private void Form1_Resize(object sender, EventArgs e)
    {
        // User interaction with the app - consider this activity
        NotifyActivity();
        
        // Minimize to tray
        if (this.WindowState == FormWindowState.Minimized)
        {
            this.Hide();
            trayIcon.Visible = true;
            
            // Optional notification
            if (_debugMode)
            {
                trayIcon.ShowBalloonTip(
                    2000, 
                    "QR Code Scanner", 
                    "Application is still running in the background.", 
                    ToolTipIcon.Info);
            }
            
            // Enter more aggressive idle mode when minimized to tray
            if (!isIdleMode)
            {
                EnterIdleMode();
            }
        }
        else if (this.WindowState == FormWindowState.Normal)
        {
            // Exit idle mode when restored
            if (isIdleMode)
            {
                ExitIdleMode();
            }
        }
    }
    
    // Add TestButton_Click event handler
    private void TestButton_Click(object sender, EventArgs e)
    {
        if (_isDisposing) return;
        
        // Notify system of user activity
        NotifyActivity();
        
        try
        {
            SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Testing scanner connectivity...\r\n");
            
            if (_serialPort == null || !_serialPort.IsOpen)
            {
                SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Serial port is not open. Attempting to reconnect...\r\n");
                AttemptSerialReconnection();
                return;
            }
            
            // Send a test command to the scanner
            // Note: This may need to be adjusted based on the specific scanner protocol
            try
            {
                // Send a simple command that most scanners will respond to
                _serialPort.WriteLine("\r");
                SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Test command sent to scanner\r\n");
                
                // Update last successful operation time
                lastSuccessfulOperation = DateTime.Now;
            }
            catch (Exception ex)
            {
                SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Error sending test command: {ex.Message}\r\n");
                AttemptSerialReconnection();
            }
            
            // Also test system health
            PerformSystemHealthCheck();
        }
        catch (Exception ex)
        {
            if (!_isDisposing)
            {
                HandleSerialException(ex, "Error during scanner test");
            }
        }
    }
    
    // Add ComSetupButton_Click event handler
    private void ComSetupButton_Click(object sender, EventArgs e)
    {
        if (_isDisposing) return;
        
        // Notify system of user activity
        NotifyActivity();
        
        try
        {
            // Create and show COM port setup dialog
            using (var setupForm = new Form())
            {
                setupForm.Text = "COM Port Setup";
                setupForm.Size = new Size(400, 350);
                setupForm.StartPosition = FormStartPosition.CenterParent;
                setupForm.FormBorderStyle = FormBorderStyle.FixedDialog;
                setupForm.MaximizeBox = false;
                setupForm.MinimizeBox = false;
                
                // Create controls
                var portLabel = new Label { Text = "Port:", Left = 20, Top = 20, Width = 100 };
                var portCombo = new ComboBox { Left = 130, Top = 20, Width = 200, DropDownStyle = ComboBoxStyle.DropDownList };
                
                var baudLabel = new Label { Text = "Baud Rate:", Left = 20, Top = 50, Width = 100 };
                var baudCombo = new ComboBox { Left = 130, Top = 50, Width = 200, DropDownStyle = ComboBoxStyle.DropDownList };
                
                var parityLabel = new Label { Text = "Parity:", Left = 20, Top = 80, Width = 100 };
                var parityCombo = new ComboBox { Left = 130, Top = 80, Width = 200, DropDownStyle = ComboBoxStyle.DropDownList };
                
                var dataBitsLabel = new Label { Text = "Data Bits:", Left = 20, Top = 110, Width = 100 };
                var dataBitsCombo = new ComboBox { Left = 130, Top = 110, Width = 200, DropDownStyle = ComboBoxStyle.DropDownList };
                
                var stopBitsLabel = new Label { Text = "Stop Bits:", Left = 20, Top = 140, Width = 100 };
                var stopBitsCombo = new ComboBox { Left = 130, Top = 140, Width = 200, DropDownStyle = ComboBoxStyle.DropDownList };
                
                var debugLabel = new Label { Text = "Debug Mode:", Left = 20, Top = 170, Width = 100 };
                var debugCheck = new CheckBox { Text = "Enable Debug", Left = 130, Top = 170, Width = 200 };
                
                var refreshButton = new Button { Text = "Refresh Ports", Left = 20, Top = 210, Width = 100 };
                var saveButton = new Button { Text = "Save", Left = 130, Top = 210, Width = 100 };
                var cancelButton = new Button { Text = "Cancel", Left = 240, Top = 210, Width = 100 };
                
                // Add event handlers for buttons
                refreshButton.Click += (s, args) => 
                {
                    try
                    {
                        portCombo.Items.Clear();
                        string[] ports = SerialPort.GetPortNames();
                        portCombo.Items.AddRange(ports);
                        if (ports.Length > 0) portCombo.SelectedIndex = 0;
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error refreshing ports: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                };
                
                saveButton.Click += (s, args) => 
                {
                    try
                    {
                        // Validate selections
                        if (portCombo.SelectedItem == null || baudCombo.SelectedItem == null ||
                            parityCombo.SelectedItem == null || dataBitsCombo.SelectedItem == null ||
                            stopBitsCombo.SelectedItem == null)
                        {
                            MessageBox.Show("Please select values for all fields.", "Validation Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            return;
                        }
                        
                        // Save settings to file
                        string settingsPath = Path.Combine(Application.StartupPath, SETTINGS_FILE);
                        
                        // Create settings content
                        List<string> settings = new List<string>
                        {
                            "# QR Code Scanner Settings",
                            "# Format: key=value",
                            "",
                            $"port={portCombo.SelectedItem}",
                            $"baudrate={baudCombo.SelectedItem}",
                            $"parity={parityCombo.SelectedItem}",
                            $"databits={dataBitsCombo.SelectedItem}",
                            $"stopbits={stopBitsCombo.SelectedItem}",
                            $"debug={debugCheck.Checked.ToString().ToLower()}"
                        };
                        
                        // Write to file
                        File.WriteAllLines(settingsPath, settings);
                        
                        // Create backup
                        BackupSettings();
                        
                        // Set debug mode
                        _debugMode = debugCheck.Checked;
                        
                        // Close dialog
                        setupForm.DialogResult = DialogResult.OK;
                        setupForm.Close();
                        
                        // Ask if user wants to reconnect now
                        var result = MessageBox.Show(
                            "Settings saved. Do you want to reconnect to the serial port now?",
                            "Reconnect",
                            MessageBoxButtons.YesNo,
                            MessageBoxIcon.Question);
                            
                        if (result == DialogResult.Yes)
                        {
                            InitializeSerialPort();
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error saving settings: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                };
                
                cancelButton.Click += (s, args) => 
                {
                    setupForm.DialogResult = DialogResult.Cancel;
                    setupForm.Close();
                };
                
                // Populate COM ports
                try
                {
                    string[] ports = SerialPort.GetPortNames();
                    portCombo.Items.AddRange(ports);
                    
                    // Select current port if available
                    if (_serialPort != null && !string.IsNullOrEmpty(_serialPort.PortName))
                    {
                        int index = portCombo.Items.IndexOf(_serialPort.PortName);
                        if (index >= 0)
                            portCombo.SelectedIndex = index;
                        else if (portCombo.Items.Count > 0)
                            portCombo.SelectedIndex = 0;
                    }
                    else if (ports.Length > 0)
                    {
                        portCombo.SelectedIndex = 0;
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error getting COM ports: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                
                // Populate baud rates
                baudCombo.Items.AddRange(new object[] { 110, 300, 1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200, 230400 });
                
                // Select current baud rate if available
                if (_serialPort != null)
                {
                    int index = baudCombo.Items.IndexOf(_serialPort.BaudRate);
                    if (index >= 0)
                        baudCombo.SelectedIndex = index;
                    else
                        baudCombo.SelectedIndex = baudCombo.Items.IndexOf(9600); // Default to 9600
                }
                else
                {
                    baudCombo.SelectedIndex = baudCombo.Items.IndexOf(9600); // Default to 9600
                }
                
                // Populate parity
                parityCombo.Items.AddRange(Enum.GetNames(typeof(Parity)));
                
                // Select current parity if available
                if (_serialPort != null)
                {
                    parityCombo.SelectedIndex = (int)_serialPort.Parity;
                }
                else
                {
                    parityCombo.SelectedIndex = (int)Parity.None; // Default to None
                }
                
                // Populate data bits
                dataBitsCombo.Items.AddRange(new object[] { 5, 6, 7, 8 });
                
                // Select current data bits if available
                if (_serialPort != null)
                {
                    int index = dataBitsCombo.Items.IndexOf(_serialPort.DataBits);
                    if (index >= 0)
                        dataBitsCombo.SelectedIndex = index;
                    else
                        dataBitsCombo.SelectedIndex = dataBitsCombo.Items.IndexOf(8); // Default to 8
                }
                else
                {
                    dataBitsCombo.SelectedIndex = dataBitsCombo.Items.IndexOf(8); // Default to 8
                }
                
                // Populate stop bits
                stopBitsCombo.Items.AddRange(Enum.GetNames(typeof(StopBits)));
                
                // Select current stop bits if available
                if (_serialPort != null)
                {
                    stopBitsCombo.SelectedIndex = (int)_serialPort.StopBits;
                }
                else
                {
                    stopBitsCombo.SelectedIndex = (int)StopBits.One; // Default to One
                }
                
                // Set debug checkbox
                debugCheck.Checked = _debugMode;
                
                // Add controls to form
                setupForm.Controls.AddRange(new Control[] {
                    portLabel, portCombo,
                    baudLabel, baudCombo,
                    parityLabel, parityCombo,
                    dataBitsLabel, dataBitsCombo,
                    stopBitsLabel, stopBitsCombo,
                    debugLabel, debugCheck,
                    refreshButton, saveButton, cancelButton
                });
                
                // Show the form
                setupForm.ShowDialog(this);
            }
        }
        catch (Exception ex)
        {
            if (!_isDisposing)
            {
                SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Error in COM setup: {ex.Message}\r\n");
            }
        }
    }
    
    // Add method to initialize tray icon
    private void InitializeTrayIcon()
    {
        // Create tray icon
        trayIcon = new NotifyIcon();
        trayIcon.Text = "QR Code Scanner";
        trayIcon.Icon = this.Icon; // Use the same icon as the form
        
        // Create context menu for tray icon
        var contextMenu = new ContextMenuStrip();
        
        // Add menu items
        var openItem = new ToolStripMenuItem("Open");
        openItem.Click += (s, e) => 
        {
            this.Show();
            this.WindowState = FormWindowState.Normal;
            this.BringToFront();
        };
        
        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (s, e) => 
        {
            // Set flag to indicate intentional shutdown
            _isDisposing = true;
            Application.Exit();
        };
        
        // Add menu items to context menu
        contextMenu.Items.Add(openItem);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(exitItem);
        
        // Assign context menu to tray icon
        trayIcon.ContextMenuStrip = contextMenu;
        
        // Add double-click handler to open application
        trayIcon.DoubleClick += (s, e) => 
        {
            this.Show();
            this.WindowState = FormWindowState.Normal;
            this.BringToFront();
        };
    }
    
    // Add method to add startup option
    private void AddStartupOption()
    {
        try
        {
            // Add startup option to context menu
            if (trayIcon != null && trayIcon.ContextMenuStrip != null)
            {
                // Check if the startup entry already exists
                bool isStartupEnabled = IsStartupEnabled();
                
                // Create menu item
                var startupItem = new ToolStripMenuItem("Start with Windows");
                startupItem.Checked = isStartupEnabled;
                startupItem.CheckOnClick = true;
                startupItem.Click += (s, e) => 
                {
                    try
                    {
                        if (startupItem.Checked)
                        {
                            // Add to startup
                            AddToStartup();
                        }
                        else
                        {
                            // Remove from startup
                            RemoveFromStartup();
                        }
                    }
                    catch (Exception ex)
                    {
                        SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Error changing startup setting: {ex.Message}\r\n");
                        startupItem.Checked = IsStartupEnabled(); // Reset to actual state
                    }
                };
                
                // Insert at beginning of menu
                trayIcon.ContextMenuStrip.Items.Insert(0, startupItem);
                trayIcon.ContextMenuStrip.Items.Insert(1, new ToolStripSeparator());
            }
        }
        catch (Exception ex)
        {
            SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Error adding startup option: {ex.Message}\r\n");
        }
    }
    
    // Check if application is set to run at startup
    private bool IsStartupEnabled()
    {
        try
        {
            // Check registry key for startup entry
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", false))
            {
                if (key != null)
                {
                    var value = key.GetValue("QRCodeScanner");
                    return value != null && value.ToString().Contains(Application.ExecutablePath);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error checking startup status: {ex.Message}");
        }
        
        return false;
    }
    
    // Add application to startup
    private void AddToStartup()
    {
        try
        {
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
            {
                if (key != null)
                {
                    // Add with /minimized argument
                    key.SetValue("QRCodeScanner", $"\"{Application.ExecutablePath}\" /minimized");
                    SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Added to Windows startup\r\n");
                }
            }
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to add to startup: {ex.Message}", ex);
        }
    }
    
    // Remove application from startup
    private void RemoveFromStartup()
    {
        try
        {
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
            {
                if (key != null)
                {
                    key.DeleteValue("QRCodeScanner", false);
                    SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Removed from Windows startup\r\n");
                }
            }
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to remove from startup: {ex.Message}", ex);
        }
    }

    // Add implementation for PerformSystemHealthCheck method
    private void PerformSystemHealthCheck()
    {
        if (_isDisposing) return;
        
        try
        {
            // Check if serial port is open
            if (_serialPort == null || !_serialPort.IsOpen)
            {
                if (!reconnectionTimer?.Enabled ?? false)
                {
                    // Port is closed and not trying to reconnect
                    SafeAppendText($"[{DateTime.Now:HH:mm:ss}] System check: Serial port is closed, initiating reconnection\r\n");
                    AttemptSerialReconnection();
                }
            }
            
            // Check memory usage
            long memoryUsage = GC.GetTotalMemory(false) / (1024 * 1024); // MB
            
            // More aggressive memory management for long-term stability
            // Perform light cleanup more frequently
            if (memoryUsage > 100) // Reduced threshold from 200MB to 100MB
            {
                if (_debugMode)
                {
                    SafeAppendText($"[{DateTime.Now:HH:mm:ss}] System check: High memory usage ({memoryUsage}MB), performing cleanup\r\n");
                }
                
                // Force garbage collection to free memory
                GC.Collect(0); // Collect gen 0 only for a lighter collection
            }
            // Do a full collection less frequently
            else if (DateTime.Now.Minute % 30 == 0 && DateTime.Now.Second < 10) // Every 30 minutes
            {
                if (_debugMode)
                {
                    SafeAppendText($"[{DateTime.Now:HH:mm:ss}] System check: Performing periodic full memory cleanup\r\n");
                }
                
                // Full cleanup
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
            
            // Check display box text length to prevent excessive memory usage
            if (displayBox.Text.Length > 50000) // Cap at ~50KB of text
            {
                if (_debugMode)
                {
                    SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Trimming display log to prevent memory growth\r\n");
                }
                
                // Keep only the most recent entries
                string[] lines = displayBox.Text.Split(new[] { "\r\n" }, StringSplitOptions.None);
                if (lines.Length > 500) // Keep last 500 lines
                {
                    string newText = string.Join("\r\n", lines.Skip(lines.Length - 500));
                    displayBox.Text = newText;
                }
            }
            
            // Check CPU usage (rough estimate)
            using (var proc = Process.GetCurrentProcess())
            {
                proc.Refresh();
                TimeSpan cpuTime = proc.TotalProcessorTime;
                
                if (_debugMode && DateTime.Now.Minute % 10 == 0) // Log every 10 minutes
                {
                    double uptimeHours = (DateTime.Now - proc.StartTime).TotalHours;
                    if (uptimeHours > 0)
                    {
                        double avgCpuPercent = cpuTime.TotalSeconds / (Environment.ProcessorCount * uptimeHours * 36); // % of one core
                        SafeAppendText($"[{DateTime.Now:HH:mm:ss}] System check: {avgCpuPercent:0.0}% avg CPU, {memoryUsage}MB memory, uptime {uptimeHours:0.0}h\r\n");
                    }
                }
            }
            
            // Apply power management optimizations
            OptimizePowerUsage();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error in system health check: {ex.Message}");
        }
    }

    // Add power management optimization method
    private DateTime lastPowerOptimization = DateTime.MinValue;
    private bool isIdleMode = false;
    private const int IDLE_THRESHOLD_MINUTES = 30; // Time without activity to enter idle mode

    private void OptimizePowerUsage()
    {
        if (_isDisposing) return;
        
        try
        {
            // Only run power optimization check every 5 minutes to avoid overhead
            if ((DateTime.Now - lastPowerOptimization).TotalMinutes < 5)
                return;
                
            lastPowerOptimization = DateTime.Now;
            
            // Check if system has been inactive
            TimeSpan idleTime = DateTime.Now - lastSuccessfulOperation;
            
            // Enter idle mode if inactive for threshold period
            if (!isIdleMode && idleTime.TotalMinutes > IDLE_THRESHOLD_MINUTES)
            {
                // Enter power-saving mode
                EnterIdleMode();
            }
            // Exit idle mode if we've seen recent activity
            else if (isIdleMode && idleTime.TotalMinutes < IDLE_THRESHOLD_MINUTES)
            {
                // Exit power-saving mode
                ExitIdleMode();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error in power optimization: {ex.Message}");
        }
    }

    // Method to enter low-power idle mode
    private Dictionary<System.Windows.Forms.Timer, int> originalTimerIntervals = new Dictionary<System.Windows.Forms.Timer, int>();

    private void EnterIdleMode()
    {
        if (_isDisposing || _isShuttingDown || isIdleMode) return;
        
        try
        {
            if (_debugMode)
            {
                SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Entering low-power idle mode after {IDLE_THRESHOLD_MINUTES} minutes of inactivity\r\n");
            }
            
            // Store original timer intervals and increase them to reduce CPU/power usage
            foreach (var timer in _timers)
            {
                if (timer != null && timer.Enabled)
                {
                    // Save original interval
                    originalTimerIntervals[timer] = timer.Interval;
                    
                    // Adjust interval based on timer type to reduce frequency
                    if (timer == watchdogTimer)
                    {
                        // Watchdog can run less frequently during idle
                        timer.Interval = 300000; // 5 minutes
                    }
                    else if (timer == heartbeatTimer)
                    {
                        // Keep heartbeat more frequent but still reduced
                        timer.Interval = 30000; // 30 seconds
                    }
                    else if (timer == _serialBufferTimer)
                    {
                        // Serial buffer timer can be slowed down
                        timer.Interval = 1000; // 1 second
                    }
                    else
                    {
                        // For any other timers, double their interval
                        timer.Interval *= 2;
                    }
                }
            }
            
            // Reduce UI update frequency
            if (periodicCaseCheckTimer != null && periodicCaseCheckTimer.Enabled)
            {
                originalTimerIntervals[periodicCaseCheckTimer] = periodicCaseCheckTimer.Interval;
                periodicCaseCheckTimer.Interval *= 5; // Much less frequent UI updates
            }
            
            // Set process priority to below normal to reduce CPU usage
            try
            {
                using (var proc = Process.GetCurrentProcess())
                {
                    proc.PriorityClass = ProcessPriorityClass.BelowNormal;
                    
                    if (_debugMode)
                    {
                        SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Process priority set to BelowNormal\r\n");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error adjusting process priority: {ex.Message}");
            }
            
            // Request lower power consumption from OS
            try
            {
                SetThreadExecutionState(EXECUTION_STATE.ES_CONTINUOUS);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error setting thread execution state: {ex.Message}");
            }
            
            // Mark as in idle mode
            isIdleMode = true;
            
            // Force garbage collection now to free memory before idle period
            ForceMemoryCollection();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error entering idle mode: {ex.Message}");
        }
    }

    // Add method to exit low-power idle mode
    private void ExitIdleMode()
    {
        if (_isDisposing || _isShuttingDown || !isIdleMode) return;
        
        try
        {
            if (_debugMode)
            {
                SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Exiting low-power mode due to activity\r\n");
            }
            
            // Restore original timer intervals
            foreach (var kvp in originalTimerIntervals)
            {
                var timer = kvp.Key;
                var originalInterval = kvp.Value;
                
                if (timer != null && !timer.IsDisposed)
                {
                    timer.Interval = originalInterval;
                }
            }
            
            // Clear the saved intervals
            originalTimerIntervals.Clear();
            
            // Set process priority back to normal
            try
            {
                using (var proc = Process.GetCurrentProcess())
                {
                    proc.PriorityClass = ProcessPriorityClass.Normal;
                    
                    if (_debugMode)
                    {
                        SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Process priority restored to Normal\r\n");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error restoring process priority: {ex.Message}");
            }
            
            // Request system stays awake when scanner is active
            try
            {
                SetThreadExecutionState(EXECUTION_STATE.ES_CONTINUOUS | EXECUTION_STATE.ES_SYSTEM_REQUIRED);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error setting thread execution state: {ex.Message}");
            }
            
            // Mark as no longer in idle mode
            isIdleMode = false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error exiting idle mode: {ex.Message}");
        }
    }

    // Add Win32 P/Invoke for power management
    [Flags]
    private enum EXECUTION_STATE : uint
    {
        ES_AWAYMODE_REQUIRED = 0x00000040,
        ES_CONTINUOUS = 0x80000000,
        ES_DISPLAY_REQUIRED = 0x00000002,
        ES_SYSTEM_REQUIRED = 0x00000001
    }
    
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern EXECUTION_STATE SetThreadExecutionState(EXECUTION_STATE esFlags);

    // Add method for forced memory management
    private void ForceMemoryCollection()
    {
        try
        {
            // Clean up large object heap
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            
            // Collect all generations
            GC.Collect();
            GC.WaitForPendingFinalizers();
            
            // Second collection to clean up anything from finalizers
            GC.Collect();
            
            // Return to default for normal operations
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.Default;
            
            if (_debugMode)
            {
                long memoryAfter = GC.GetTotalMemory(false) / (1024 * 1024);
                SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Memory cleanup complete, current usage: {memoryAfter}MB\r\n");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error in ForceMemoryCollection: {ex.Message}");
        }
    }

    // Add handler for form activation
    private void Form1_Activated(object sender, EventArgs e)
    {
        // User has focused the app - consider this activity
        NotifyActivity();
    }

    // Add method to notify of activity to prevent premature idle mode
    public void NotifyActivity()
    {
        if (_isDisposing) return;
        
        // Update last successful operation time
        lastSuccessfulOperation = DateTime.Now;
        
        // If in idle mode, exit it immediately
        if (isIdleMode)
        {
            ExitIdleMode();
        }
    }

    // Add system power event handling
    private void InitializeSystemEvents()
    {
        try
        {
            // Register for system power events
            SystemEvents.PowerModeChanged += SystemEvents_PowerModeChanged;
            
            if (_debugMode)
            {
                SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Registered for system power events\r\n");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error registering for system events: {ex.Message}");
        }
    }
    
    // Handle system power events
    private void SystemEvents_PowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        try
        {
            // Handle different power mode changes
            switch (e.Mode)
            {
                case PowerModes.Suspend:
                    // System is about to enter sleep mode
                    SafeAppendText($"[{DateTime.Now:HH:mm:ss}] System entering sleep mode\r\n");
                    
                    // Close serial port cleanly before sleep
                    CloseSerialPort();
                    break;
                    
                case PowerModes.Resume:
                    // System is resuming from sleep mode
                    SafeAppendText($"[{DateTime.Now:HH:mm:ss}] System resuming from sleep mode\r\n");
                    
                    // Wait a moment for devices to initialize
                    Task.Delay(5000).ContinueWith(_ =>
                    {
                        if (_isDisposing || _isShuttingDown) return;
                        
                        try
                        {
                            // Re-initialize serial port
                            SafeAppendText($"[{DateTime.Now:HH:mm:ss}] Reconnecting serial port after resume\r\n");
                            InitializeSerialPort();
                            
                            // Reset scanner state
                            ResetScannerState();
                            
                            // Notify of activity
                            NotifyActivity();
                            
                            // Ensure system stays awake now that we're running again
                            SetThreadExecutionState(EXECUTION_STATE.ES_CONTINUOUS | EXECUTION_STATE.ES_SYSTEM_REQUIRED);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Error during resume reconnection: {ex.Message}");
                        }
                    }, TaskScheduler.Default);
                    break;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error in power mode handler: {ex.Message}");
        }
    }
    
    // Clean up system events
    private void CleanupSystemEvents()
    {
        try
        {
            // Unregister from system events
            SystemEvents.PowerModeChanged -= SystemEvents_PowerModeChanged;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error unregistering from system events: {ex.Message}");
        }
    }
}
