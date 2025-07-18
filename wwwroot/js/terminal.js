window.terminalConnections = {};

window.initializeTerminal = (sessionId, dotNetRef) => {
    const protocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
    const wsUrl = `${protocol}//${window.location.host}/ws/terminal?sessionId=${sessionId}`;
    
    const socket = new WebSocket(wsUrl);
    
    socket.onopen = () => {
        console.log(`Terminal ${sessionId} connected`);
    };
    
    socket.onmessage = (event) => {
        console.log('Received message:', event.data);
        const message = JSON.parse(event.data);
        console.log('Parsed message:', message);
        dotNetRef.invokeMethodAsync('OnTerminalMessage', event.data);
    };
    
    socket.onclose = () => {
        console.log(`Terminal ${sessionId} disconnected`);
        delete window.terminalConnections[sessionId];
    };
    
    socket.onerror = (error) => {
        console.error(`Terminal ${sessionId} error:`, error);
        appendTerminalOutput(sessionId, 'error', 'Connection error occurred\n');
    };
    
    window.terminalConnections[sessionId] = {
        socket: socket,
        dotNetRef: dotNetRef
    };
};

window.sendTerminalCommand = (sessionId, command) => {
    console.log('Sending command:', command);
    const connection = window.terminalConnections[sessionId];
    if (connection && connection.socket.readyState === WebSocket.OPEN) {
        const message = {
            type: 'input',
            content: command
        };
        console.log('Sending message:', message);
        connection.socket.send(JSON.stringify(message));
    } else {
        console.error('Connection not available or not open:', connection);
    }
};

window.closeTerminalConnection = (sessionId) => {
    const connection = window.terminalConnections[sessionId];
    if (connection) {
        if (connection.socket.readyState === WebSocket.OPEN) {
            connection.socket.close();
        }
        delete window.terminalConnections[sessionId];
    }
};

window.getTerminalInputValue = (sessionId) => {
    const input = document.getElementById(`terminal-input-${sessionId}`);
    return input ? input.value : '';
};

window.setTerminalInputValue = (sessionId, value) => {
    const input = document.getElementById(`terminal-input-${sessionId}`);
    if (input) {
        input.value = value;
        // Move cursor to end
        input.setSelectionRange(value.length, value.length);
    }
};

window.clearTerminalInput = (sessionId) => {
    const input = document.getElementById(`terminal-input-${sessionId}`);
    if (input) {
        input.value = '';
    }
};

window.appendTerminalOutput = (sessionId, type, content) => {
    const output = document.getElementById(`terminal-output-${sessionId}`);
    if (output) {
        let className = '';
        switch (type) {
            case 'error':
                className = 'terminal-error';
                break;
            case 'success':
                className = 'terminal-success';
                break;
            case 'warning':
                className = 'terminal-warning';
                break;
            case 'clear':
                output.innerHTML = '';
                return;
            case 'prompt':
                // For prompt messages, we'll add them to output but not as a new line
                const promptSpan = document.createElement('span');
                promptSpan.textContent = content;
                promptSpan.className = 'terminal-prompt-output';
                output.appendChild(promptSpan);
                
                // Auto-scroll to bottom
                const container = output.closest('.terminal-body');
                if (container) {
                    container.scrollTop = container.scrollHeight;
                }
                return;
        }
        
        const span = document.createElement('span');
        if (className) {
            span.className = className;
        }
        span.textContent = content;
        output.appendChild(span);
        
        // Auto-scroll to bottom
        const container = output.closest('.terminal-body');
        if (container) {
            container.scrollTop = container.scrollHeight;
        }
    }
};

// Focus terminal input when clicking anywhere in the terminal
document.addEventListener('click', (e) => {
    const terminal = e.target.closest('.terminal-container');
    if (terminal) {
        const sessionId = terminal.querySelector('.terminal-input').id.replace('terminal-input-', '');
        const input = document.getElementById(`terminal-input-${sessionId}`);
        if (input) {
            input.focus();
        }
    }
});
