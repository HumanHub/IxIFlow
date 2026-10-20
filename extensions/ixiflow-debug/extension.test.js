'use strict';
// spec: G-10
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');

function factory(platform) {
    let registered;
    const vscode = {
        debug: { registerDebugAdapterDescriptorFactory(type, descriptor) {
            assert.equal(type, 'ixiflow'); registered = descriptor; return { dispose() {} };
        } },
        DebugAdapterServer: class { constructor(port, host) { Object.assign(this, { port, host }); } },
        DebugAdapterNamedPipeServer: class { constructor(pipe) { this.pipe = pipe; } }
    };
    const context = { module: { exports: {} }, process: { platform }, URL,
        require(name) { return name === 'vscode' ? vscode : require(name); } };
    vm.runInNewContext(fs.readFileSync(path.join(__dirname, 'extension.js'), 'utf8'), context);
    const subscriptions = [];
    context.module.exports.activate({ subscriptions });
    assert.equal(subscriptions.length, 1);
    return endpoint => registered.createDebugAdapterDescriptor({ configuration: { endpoint } });
}

test('G-10 descriptor connects to the configured current-user pipe or IPv4/IPv6 loopback port', () => {
    const connect = factory('win32');
    assert.equal(connect('pipe:ixiflow-debug').pipe, '\\\\.\\pipe\\ixiflow-debug');
    assert.equal(connect('tcp://127.0.0.1:4711').host, '127.0.0.1');
    assert.equal(connect('tcp://[::1]:4711').host, '::1');
    assert.equal(connect('tcp://127.0.0.1:4711').port, 4711);
    assert.match(factory('linux')('pipe:ixiflow-debug').pipe, /CoreFxPipe_ixiflow-debug$/);
});

test('G-10 descriptor refuses external endpoints, URL credentials and arbitrary pipe paths', () => {
    const connect = factory('win32');
    for (const endpoint of ['tcp://example.com:4711', 'tcp://0.0.0.0:4711', 'tcp://secret@127.0.0.1:4711',
        'tcp://127.0.0.1:4711?token=secret', 'https://127.0.0.1:4711', 'pipe:../other', 'pipe:']) {
        assert.throws(() => connect(endpoint));
    }
});
