using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CopilotConnectorGui.Services
{
    public class WebTerminalService
    {
        private readonly ILogger<WebTerminalService> _logger;
        private readonly ConcurrentDictionary<string, TerminalSession> _sessions = new();

        public WebTerminalService(ILogger<WebTerminalService> logger)
        {
            _logger = logger;
        }

        public async Task HandleWebSocketAsync(WebSocket webSocket, string sessionId)
        {
            _logger.LogInformation("Starting WebSocket session {SessionId}", sessionId);
            var session = GetOrCreateSession(sessionId);
            session.WebSocket = webSocket;

            try
            {
                _logger.LogInformation("Sending welcome message to session {SessionId}", sessionId);
                // Send welcome message
                await SendMessageAsync(webSocket, new TerminalMessage
                {
                    Type = "output",
                    Content = "🚀 Azure CLI Web Terminal\r\n" +
                             "Type 'az login' to authenticate with Azure\r\n" +
                             "Type 'help' for available commands\r\n" +
                             "Type 'exit' to close terminal\r\n\r\n"
                });

                // Send initial prompt
                await SendMessageAsync(webSocket, new TerminalMessage
                {
                    Type = "prompt",
                    Content = "PS > "
                });

                _logger.LogInformation("Starting message loop for session {SessionId}", sessionId);
                var buffer = new byte[4096];
                while (webSocket.State == WebSocketState.Open)
                {
                    var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    
                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        _logger.LogInformation("Received message from session {SessionId}: {Message}", sessionId, message);
                        
                        var terminalMessage = JsonSerializer.Deserialize<TerminalMessage>(message);
                        _logger.LogInformation("Parsed message type: {Type}, content: {Content}", terminalMessage?.Type, terminalMessage?.Content);
                        
                        if (terminalMessage?.Type == "input")
                        {
                            _logger.LogInformation("Processing command for session {SessionId}: {Command}", sessionId, terminalMessage.Content);
                            await ProcessCommandAsync(session, terminalMessage.Content);
                        }
                    }
                    else if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _logger.LogInformation("WebSocket close message received for session {SessionId}", sessionId);
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "WebSocket error for session {SessionId}", sessionId);
            }
            finally
            {
                _logger.LogInformation("Cleaning up session {SessionId}", sessionId);
                _sessions.TryRemove(sessionId, out _);
                session?.Dispose();
            }
        }

        private TerminalSession GetOrCreateSession(string sessionId)
        {
            return _sessions.GetOrAdd(sessionId, _ => new TerminalSession
            {
                Id = sessionId,
                CreatedAt = DateTime.UtcNow,
                WorkingDirectory = Directory.GetCurrentDirectory()
            });
        }

        private async Task ProcessCommandAsync(TerminalSession session, string command)
        {
            _logger.LogInformation("ProcessCommandAsync called with command: '{Command}'", command);
            
            if (string.IsNullOrWhiteSpace(command))
            {
                _logger.LogInformation("Empty command, sending prompt");
                await SendPromptAsync(session);
                return;
            }

            command = command.Trim();
            _logger.LogInformation("Processing trimmed command: '{Command}'", command);

            // Handle built-in commands
            switch (command.ToLower())
            {
                case "exit":
                    _logger.LogInformation("Processing exit command");
                    await SendMessageAsync(session.WebSocket!, new TerminalMessage
                    {
                        Type = "output",
                        Content = "Goodbye!\r\n"
                    });
                    await session.WebSocket!.CloseAsync(WebSocketCloseStatus.NormalClosure, "Session ended", CancellationToken.None);
                    return;

                case "clear":
                    _logger.LogInformation("Processing clear command");
                    await SendMessageAsync(session.WebSocket!, new TerminalMessage
                    {
                        Type = "clear",
                        Content = ""
                    });
                    await SendPromptAsync(session);
                    return;

                case "help":
                    _logger.LogInformation("Processing help command");
                    await SendMessageAsync(session.WebSocket!, new TerminalMessage
                    {
                        Type = "output",
                        Content = "Available commands:\r\n" +
                                 "  az login                 - Login to Azure\r\n" +
                                 "  az account show          - Show current account\r\n" +
                                 "  az ad app create         - Create app registration\r\n" +
                                 "  bootstrap                - Run full bootstrap process\r\n" +
                                 "  clear                    - Clear terminal\r\n" +
                                 "  exit                     - Close terminal\r\n" +
                                 "  help                     - Show this help\r\n\r\n"
                    });
                    await SendPromptAsync(session);
                    return;

                case "bootstrap":
                    _logger.LogInformation("Processing bootstrap command");
                    await RunBootstrapCommandAsync(session);
                    return;
            }

            // Handle Azure CLI commands
            if (command.StartsWith("az "))
            {
                _logger.LogInformation("Processing Azure CLI command: {Command}", command);
                await ExecuteAzureCliCommandAsync(session, command);
            }
            else if (command.StartsWith("powershell ") || command.StartsWith("pwsh "))
            {
                _logger.LogInformation("Processing PowerShell command: {Command}", command);
                await ExecutePowerShellCommandAsync(session, command);
            }
            else
            {
                _logger.LogInformation("Unknown command: {Command}", command);
                await SendMessageAsync(session.WebSocket!, new TerminalMessage
                {
                    Type = "output",
                    Content = $"Command not recognized: {command}\r\n" +
                             "Type 'help' for available commands or use 'az' prefix for Azure CLI commands.\r\n\r\n"
                });
                await SendPromptAsync(session);
            }
        }

        private async Task ExecuteAzureCliCommandAsync(TerminalSession session, string command)
        {
            try
            {
                await SendMessageAsync(session.WebSocket!, new TerminalMessage
                {
                    Type = "output",
                    Content = $"$ {command}\r\n"
                });

                // Handle special interactive commands
                var azCommand = command.Substring(3).Trim(); // Remove "az " prefix
                
                if (azCommand.StartsWith("login"))
                {
                    await HandleAzLoginAsync(session, azCommand);
                    return;
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = "az",
                    Arguments = azCommand,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = session.WorkingDirectory
                };

                using var process = new Process { StartInfo = startInfo };
                
                process.OutputDataReceived += async (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        await SendMessageAsync(session.WebSocket!, new TerminalMessage
                        {
                            Type = "output",
                            Content = e.Data + "\r\n"
                        });
                    }
                };

                process.ErrorDataReceived += async (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        await SendMessageAsync(session.WebSocket!, new TerminalMessage
                        {
                            Type = "error",
                            Content = e.Data + "\r\n"
                        });
                    }
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                // Set a timeout for long-running commands
                var timeoutTask = Task.Delay(30000); // 30 seconds timeout
                var processTask = process.WaitForExitAsync();
                
                var completedTask = await Task.WhenAny(processTask, timeoutTask);
                
                if (completedTask == timeoutTask)
                {
                    await SendMessageAsync(session.WebSocket!, new TerminalMessage
                    {
                        Type = "warning",
                        Content = "Command timed out after 30 seconds. This might be an interactive command.\r\n"
                    });
                    
                    if (!process.HasExited)
                    {
                        process.Kill();
                    }
                }
                else if (process.ExitCode != 0)
                {
                    await SendMessageAsync(session.WebSocket!, new TerminalMessage
                    {
                        Type = "error",
                        Content = $"Command failed with exit code {process.ExitCode}\r\n"
                    });
                }
            }
            catch (Exception ex)
            {
                await SendMessageAsync(session.WebSocket!, new TerminalMessage
                {
                    Type = "error",
                    Content = $"Error executing command: {ex.Message}\r\n"
                });
            }

            await SendPromptAsync(session);
        }

        private async Task HandleAzLoginAsync(TerminalSession session, string azCommand)
        {
            try
            {
                await SendMessageAsync(session.WebSocket!, new TerminalMessage
                {
                    Type = "output",
                    Content = "🔐 Starting Azure CLI login process...\r\n\r\n"
                });

                // Check if already logged in first
                var checkResult = await RunCommandAsync("az", "account show --output json");
                if (checkResult.exitCode == 0)
                {
                    await SendMessageAsync(session.WebSocket!, new TerminalMessage
                    {
                        Type = "success",
                        Content = "✅ Already logged in to Azure!\r\n"
                    });
                    
                    await SendMessageAsync(session.WebSocket!, new TerminalMessage
                    {
                        Type = "output",
                        Content = checkResult.output + "\r\n"
                    });
                    return;
                }

                await SendMessageAsync(session.WebSocket!, new TerminalMessage
                {
                    Type = "output",
                    Content = "🌐 Opening browser for authentication...\r\n" +
                             "Please complete the login in the browser window that opened.\r\n" +
                             "If no browser opened, try running: az login --use-device-code\r\n\r\n"
                });

                // Run az login in the background
                var loginResult = await RunCommandAsync("az", azCommand);
                
                if (loginResult.exitCode == 0)
                {
                    await SendMessageAsync(session.WebSocket!, new TerminalMessage
                    {
                        Type = "success",
                        Content = "✅ Azure CLI login successful!\r\n"
                    });
                    
                    if (!string.IsNullOrEmpty(loginResult.output))
                    {
                        await SendMessageAsync(session.WebSocket!, new TerminalMessage
                        {
                            Type = "output",
                            Content = loginResult.output + "\r\n"
                        });
                    }

                    // Show current account info
                    await SendMessageAsync(session.WebSocket!, new TerminalMessage
                    {
                        Type = "output",
                        Content = "Current account information:\r\n"
                    });
                    
                    var accountResult = await RunCommandAsync("az", "account show --output table");
                    if (accountResult.exitCode == 0)
                    {
                        await SendMessageAsync(session.WebSocket!, new TerminalMessage
                        {
                            Type = "output",
                            Content = accountResult.output + "\r\n"
                        });
                    }
                }
                else
                {
                    await SendMessageAsync(session.WebSocket!, new TerminalMessage
                    {
                        Type = "error",
                        Content = "❌ Azure CLI login failed.\r\n"
                    });
                    
                    if (!string.IsNullOrEmpty(loginResult.error))
                    {
                        await SendMessageAsync(session.WebSocket!, new TerminalMessage
                        {
                            Type = "error",
                            Content = loginResult.error + "\r\n"
                        });
                    }

                    await SendMessageAsync(session.WebSocket!, new TerminalMessage
                    {
                        Type = "output",
                        Content = "💡 Try alternatives:\r\n" +
                                 "  az login --use-device-code  (for device code flow)\r\n" +
                                 "  az login --tenant YOUR_TENANT_ID  (for specific tenant)\r\n\r\n"
                    });
                }
            }
            catch (Exception ex)
            {
                await SendMessageAsync(session.WebSocket!, new TerminalMessage
                {
                    Type = "error",
                    Content = $"Error during login: {ex.Message}\r\n"
                });
            }
        }

        private async Task ExecutePowerShellCommandAsync(TerminalSession session, string command)
        {
            try
            {
                await SendMessageAsync(session.WebSocket!, new TerminalMessage
                {
                    Type = "output",
                    Content = $"PS> {command}\r\n"
                });

                var isCore = command.StartsWith("pwsh ");
                var executable = isCore ? "pwsh" : "powershell";
                var args = command.Substring(isCore ? 5 : 11); // Remove prefix

                var startInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = $"-Command \"{args}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = session.WorkingDirectory
                };

                using var process = new Process { StartInfo = startInfo };
                process.Start();

                var output = await process.StandardOutput.ReadToEndAsync();
                var error = await process.StandardError.ReadToEndAsync();

                await process.WaitForExitAsync();

                if (!string.IsNullOrEmpty(output))
                {
                    await SendMessageAsync(session.WebSocket!, new TerminalMessage
                    {
                        Type = "output",
                        Content = output + "\r\n"
                    });
                }

                if (!string.IsNullOrEmpty(error))
                {
                    await SendMessageAsync(session.WebSocket!, new TerminalMessage
                    {
                        Type = "error",
                        Content = error + "\r\n"
                    });
                }
            }
            catch (Exception ex)
            {
                await SendMessageAsync(session.WebSocket!, new TerminalMessage
                {
                    Type = "error",
                    Content = $"Error executing PowerShell command: {ex.Message}\r\n"
                });
            }

            await SendPromptAsync(session);
        }

        private async Task<(string output, string error, int exitCode)> RunCommandAsync(string executable, string arguments)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = new Process { StartInfo = startInfo };
                process.Start();

                var output = await process.StandardOutput.ReadToEndAsync();
                var error = await process.StandardError.ReadToEndAsync();

                await process.WaitForExitAsync();

                return (output, error, process.ExitCode);
            }
            catch (Exception ex)
            {
                return ("", ex.Message, -1);
            }
        }

        private async Task RunBootstrapCommandAsync(TerminalSession session)
        {
            await SendMessageAsync(session.WebSocket!, new TerminalMessage
            {
                Type = "output",
                Content = "🚀 Starting Azure CLI Bootstrap Process...\r\n\r\n"
            });

            // Step 1: Check Azure CLI
            await SendMessageAsync(session.WebSocket!, new TerminalMessage
            {
                Type = "output",
                Content = "Step 1: Checking Azure CLI installation...\r\n"
            });

            await ExecuteAzureCliCommandAsync(session, "az --version");

            // Step 2: Check login status
            await SendMessageAsync(session.WebSocket!, new TerminalMessage
            {
                Type = "output",
                Content = "Step 2: Checking login status...\r\n"
            });

            await ExecuteAzureCliCommandAsync(session, "az account show");

            // Step 3: Create app registration
            await SendMessageAsync(session.WebSocket!, new TerminalMessage
            {
                Type = "output",
                Content = "Step 3: Creating app registration...\r\n"
            });

            var appName = $"Copilot-Connector-Bootstrap-{DateTime.Now:yyyyMMdd-HHmmss}";
            await ExecuteAzureCliCommandAsync(session, $"az ad app create --display-name \"{appName}\" --web-redirect-uris https://localhost:5001/signin-oidc --output table");

            await SendMessageAsync(session.WebSocket!, new TerminalMessage
            {
                Type = "output",
                Content = "\r\n✅ Bootstrap process completed!\r\n" +
                         "Next steps:\r\n" +
                         "1. Note the Application ID from above\r\n" +
                         "2. Create a client secret\r\n" +
                         "3. Update appsettings.json\r\n" +
                         "4. Restart the application\r\n\r\n"
            });
        }

        private async Task SendPromptAsync(TerminalSession session)
        {
            await SendMessageAsync(session.WebSocket!, new TerminalMessage
            {
                Type = "prompt",
                Content = "PS > "
            });
        }

        private async Task SendMessageAsync(WebSocket webSocket, TerminalMessage message)
        {
            try
            {
                _logger.LogInformation("Sending message - Type: {Type}, Content: {Content}", message.Type, message.Content?.Substring(0, Math.Min(message.Content.Length, 100)));
                
                if (webSocket.State == WebSocketState.Open)
                {
                    var json = JsonSerializer.Serialize(message);
                    var bytes = Encoding.UTF8.GetBytes(json);
                    await webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
                    _logger.LogInformation("Message sent successfully");
                }
                else
                {
                    _logger.LogWarning("WebSocket is not open. State: {State}", webSocket.State);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending message");
            }
        }
    }

    public class TerminalSession : IDisposable
    {
        public string Id { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public string WorkingDirectory { get; set; } = string.Empty;
        public WebSocket? WebSocket { get; set; }

        public void Dispose()
        {
            WebSocket?.Dispose();
        }
    }

    public class TerminalMessage
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty; // "output", "error", "input", "prompt", "clear"
        
        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;
    }
}
