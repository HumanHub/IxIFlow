'use strict';

const vscode = require('vscode');
const os = require('os');
const path = require('path');

function activate(context) {
    context.subscriptions.push(vscode.debug.registerDebugAdapterDescriptorFactory('ixiflow', {
        createDebugAdapterDescriptor(session) {
            const endpoint = session.configuration.endpoint;
            if (typeof endpoint !== 'string') throw new Error('Configure an IxIFlow endpoint.');
            if (endpoint.startsWith('pipe:')) {
                const name = endpoint.slice(5);
                if (!name || /[\\/:]/.test(name)) throw new Error('Use pipe:name with a simple pipe name.');
                const pipePath = process.platform === 'win32'
                    ? '\\\\.\\pipe\\' + name : path.join(os.tmpdir(), 'CoreFxPipe_' + name);
                return new vscode.DebugAdapterNamedPipeServer(pipePath);
            }
            const address = new URL(endpoint);
            if (address.protocol !== 'tcp:' || !['127.0.0.1', '[::1]'].includes(address.hostname) ||
                !address.port || address.username || address.password || address.search || address.hash) {
                throw new Error('Use tcp://127.0.0.1:port or tcp://[::1]:port.');
            }
            return new vscode.DebugAdapterServer(Number(address.port), address.hostname.replace(/^\[|\]$/g, ''));
        }
    }));
}

module.exports = { activate };
