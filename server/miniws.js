'use strict';

// En liten WebSocket-server (RFC 6455) så att vi inte behöver installera några paket.
// Fungerar ungefär som paketet "ws".

const crypto = require('crypto');
const { EventEmitter } = require('events');

const GUID = '258EAFA5-E914-47DA-95CA-C5AB0DC85B11';
const OPEN = 1;
const CLOSING = 2;
const CLOSED = 3;

class WebSocket extends EventEmitter {
  constructor(socket, maxPayload) {
    super();
    this.socket = socket;
    this.maxPayload = maxPayload;
    this.readyState = OPEN;
    this.buffer = Buffer.alloc(0);
    this.fragments = [];
    this.fragmentSize = 0;
    this.fragmentOpcode = 0;
    this._finished = false;
    this._closeTimer = null;

    socket.setNoDelay(true);
    socket.on('data', (chunk) => this._onData(chunk));
    socket.on('close', () => this._finish());
    socket.on('error', () => this._finish());
  }

  // Skicka

  _sendFrame(opcode, payload) {
    if (this.socket.destroyed || !this.socket.writable) return;
    const len = payload.length;
    let header;
    if (len < 126) {
      header = Buffer.alloc(2);
      header[1] = len;
    } else if (len < 65536) {
      header = Buffer.alloc(4);
      header[1] = 126;
      header.writeUInt16BE(len, 2);
    } else {
      header = Buffer.alloc(10);
      header[1] = 127;
      header.writeBigUInt64BE(BigInt(len), 2);
    }
    header[0] = 0x80 | opcode; // FIN + opcode, serverramar är aldrig maskade
    this.socket.write(Buffer.concat([header, payload]));
  }

  send(data) {
    if (this.readyState !== OPEN) return;
    this._sendFrame(0x1, Buffer.from(String(data), 'utf8'));
  }

  ping() {
    if (this.readyState === OPEN) this._sendFrame(0x9, Buffer.alloc(0));
  }

  close(code = 1000) {
    if (this.readyState !== OPEN) return;
    this.readyState = CLOSING;
    const payload = Buffer.alloc(2);
    payload.writeUInt16BE(code, 0);
    this._sendFrame(0x8, payload);
    this.socket.end();
    // Om klienten inte svarar stänger vi hårt efter en stund
    this._closeTimer = setTimeout(() => this.socket.destroy(), 2000);
    this._closeTimer.unref();
  }

  terminate() {
    this.readyState = CLOSED;
    this.socket.destroy();
  }

  // Ta emot

  _onData(chunk) {
    if (this.readyState !== OPEN) return;
    this.buffer = this.buffer.length ? Buffer.concat([this.buffer, chunk]) : chunk;

    while (this.readyState === OPEN) {
      if (this.buffer.length < 2) return;
      const b0 = this.buffer[0];
      const b1 = this.buffer[1];
      const fin = (b0 & 0x80) !== 0;
      const rsv = b0 & 0x70;
      const opcode = b0 & 0x0f;
      const masked = (b1 & 0x80) !== 0;
      let len = b1 & 0x7f;
      let offset = 2;

      if (rsv !== 0 || !masked) return this.close(1002); // protokollfel: klientramar måste vara maskade

      if (len === 126) {
        if (this.buffer.length < 4) return;
        len = this.buffer.readUInt16BE(2);
        offset = 4;
      } else if (len === 127) {
        if (this.buffer.length < 10) return;
        const big = this.buffer.readBigUInt64BE(2);
        if (big > BigInt(this.maxPayload)) return this.close(1009);
        len = Number(big);
        offset = 10;
      }
      if (len > this.maxPayload) return this.close(1009); // för stort meddelande
      if (this.buffer.length < offset + 4 + len) return;  // vänta på resten

      const mask = this.buffer.subarray(offset, offset + 4);
      const payload = Buffer.from(this.buffer.subarray(offset + 4, offset + 4 + len));
      for (let i = 0; i < len; i++) payload[i] ^= mask[i & 3];
      this.buffer = this.buffer.subarray(offset + 4 + len);

      this._onFrame(fin, opcode, payload);
    }
  }

  _onFrame(fin, opcode, payload) {
    // Kontrollramar: close, ping, pong
    if (opcode >= 0x8) {
      if (!fin || payload.length > 125) return this.close(1002);
      if (opcode === 0x8) return this.close(1000);               // klienten vill stänga
      if (opcode === 0x9) return this._sendFrame(0xa, payload);  // svara på ping
      if (opcode === 0xa) return this.emit('pong');
      return this.close(1002);
    }

    // Dataramar (text, binär, fortsättning)
    if (opcode === 0x0) {
      if (this.fragmentOpcode === 0) return this.close(1002);
    } else {
      if (this.fragmentOpcode !== 0) return this.close(1002);
      if (opcode !== 0x1 && opcode !== 0x2) return this.close(1002);
      this.fragmentOpcode = opcode;
    }

    this.fragments.push(payload);
    this.fragmentSize += payload.length;
    if (this.fragmentSize > this.maxPayload) return this.close(1009);

    if (fin) {
      const data = Buffer.concat(this.fragments);
      const isBinary = this.fragmentOpcode === 0x2;
      this.fragments = [];
      this.fragmentSize = 0;
      this.fragmentOpcode = 0;
      this.emit('message', data, isBinary);
    }
  }

  _finish() {
    if (this._finished) return;
    this._finished = true;
    this.readyState = CLOSED;
    clearTimeout(this._closeTimer);
    this.emit('close');
  }
}

class WebSocketServer extends EventEmitter {
  constructor({ server, maxPayload = 1048576 }) {
    super();
    this.clients = new Set();
    this.maxPayload = maxPayload;
    server.on('upgrade', (req, socket, head) => this._upgrade(req, socket, head));
  }

  _upgrade(req, socket, head) {
    const key = req.headers['sec-websocket-key'];
    const isUpgrade = String(req.headers.upgrade || '').toLowerCase() === 'websocket';
    if (!isUpgrade || !key || req.headers['sec-websocket-version'] !== '13') {
      socket.end('HTTP/1.1 400 Bad Request\r\nConnection: close\r\n\r\n');
      return;
    }

    const accept = crypto.createHash('sha1').update(key + GUID).digest('base64');
    socket.write(
      [
        'HTTP/1.1 101 Switching Protocols',
        'Upgrade: websocket',
        'Connection: Upgrade',
        `Sec-WebSocket-Accept: ${accept}`,
        '',
        '',
      ].join('\r\n')
    );

    const ws = new WebSocket(socket, this.maxPayload);
    this.clients.add(ws);
    ws.on('close', () => this.clients.delete(ws));
    this.emit('connection', ws, req);
    if (head && head.length) ws._onData(head);
  }
}

module.exports = { WebSocketServer, WebSocket };
