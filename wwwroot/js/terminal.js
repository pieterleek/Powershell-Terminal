/*
=============================================================================
|                         PowerShell Web Terminal                           |
=============================================================================
|                                                                           |
|   Crafted, coded, and approved by: Pieter Leek                            |
|                                                                           |
|   Status: Works on my machine (so it's probably your fault).              |
|                                                                           |
|   Warning: Contains traces of teacher humor and excessive amounts         |
|            of caffeine.                                                   |
|                                                                           |
|   If you read this text you have a good security mindset. Grade +1        |
|                                                                           |
|   P.S. Don't try to 'rm -rf' the server. I have backups,                  |
|        but I also hold your grade.                                        |
|                                                                           |
=============================================================================
*/

let cmdHistory = [];
let historyIndex = -1;

function handleKey(event) {
    const cmdInput = document.getElementById('command');
    if (event.key === 'Enter' && !event.shiftKey) {
        event.preventDefault(); 
        runCmd();
    } else if (event.key === 'ArrowUp' && cmdInput.selectionStart === 0) {
        if (cmdHistory.length > 0 && historyIndex < cmdHistory.length - 1) {
            historyIndex++;
            cmdInput.value = cmdHistory[cmdHistory.length - 1 - historyIndex];
        }
        event.preventDefault();
    } else if (event.key === 'ArrowDown' && cmdInput.selectionEnd === cmdInput.value.length) {
        if (historyIndex > 0) {
            historyIndex--;
            cmdInput.value = cmdHistory[cmdHistory.length - 1 - historyIndex];
        } else if (historyIndex === 0) {
            historyIndex = -1;
            cmdInput.value = ''; 
        }
        event.preventDefault();
    }
}

async function runCmd() {
    const cmdInput = document.getElementById('command');
    const command = cmdInput.value;
    if (!command.trim()) return;

    cmdHistory.push(command);
    historyIndex = -1;

    const outputElement = document.getElementById('output');
    outputElement.textContent += `PS> ${command}\n`;
    cmdInput.value = ''; 

    const res = await fetch('/run', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ command })
    });

    const data = await res.json();

    if (data.output) outputElement.textContent += data.output + "\n";
    
    if (data.warnings && data.warnings.length > 0) {
        data.warnings.forEach(w => { outputElement.innerHTML += `<span class="warning">WAARSCHUWING: ${w}</span>\n`; });
    }

    if (data.errors && data.errors.length > 0) {
        data.errors.forEach(e => { outputElement.innerHTML += `<span class="error">${e}</span>\n`; });
    }

    // Dit zorgt dat de terminal altijd automatisch naar beneden scrolt
    window.scrollTo(0, document.body.scrollHeight);
}