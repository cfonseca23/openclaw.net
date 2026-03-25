import makeWASocket, {
  useMultiFileAuthState,
  DisconnectReason,
  fetchLatestBaileysVersion,
  makeCacheableSignalKeyStore,
  isJidGroup,
  jidNormalizedUser,
} from "@whiskeysockets/baileys";
import { Boom } from "@hapi/boom";
import pino from "pino";
import { join } from "path";
import { mkdir } from "fs/promises";
import { sendNotification } from "./protocol.mjs";
import { downloadInboundMedia, prepareOutboundMedia } from "./media.mjs";
import { mapInboundMessage } from "./messages.mjs";

export class BaileysSession {
  constructor(accountConfig, globalConfig, channelId) {
    this.accountId = accountConfig.accountId || "default";
    this.sessionPath = accountConfig.sessionPath || "./session/default";
    this.deviceName = accountConfig.deviceName || "OpenClaw";
    this.pairingMode = accountConfig.pairingMode || "qr";
    this.phoneNumber = accountConfig.phoneNumber || null;
    this.sendReadReceipts = accountConfig.sendReadReceipts ?? true;
    this.ackReaction = accountConfig.ackReaction ?? false;
    this.mediaCachePath =
      accountConfig.mediaCachePath ||
      globalConfig.mediaCachePath ||
      join(globalConfig.storagePath || "./memory/whatsapp-worker", "media-cache");
    this.proxy = accountConfig.proxy || globalConfig.proxy || null;
    this.channelId = channelId;
    this.selfId = null;
    this.sock = null;
    this._stopped = false;
    this._reconnectAttempt = 0;
    this._logger = pino({ level: "silent" });
  }

  async start() {
    await mkdir(this.sessionPath, { recursive: true });
    const { state, saveCreds } = await useMultiFileAuthState(this.sessionPath);

    const { version } = await fetchLatestBaileysVersion();

    this.sock = makeWASocket({
      version,
      auth: {
        creds: state.creds,
        keys: makeCacheableSignalKeyStore(state.keys, this._logger),
      },
      browser: [this.deviceName, "Desktop", "1.0"],
      printQRInTerminal: false,
      logger: this._logger,
    });

    this.sock.ev.on("creds.update", saveCreds);

    this.sock.ev.on("connection.update", (update) => {
      this._handleConnectionUpdate(update);
    });

    this.sock.ev.on("messages.upsert", ({ messages, type }) => {
      if (type !== "notify") return;
      for (const msg of messages) {
        if (msg.key?.fromMe) continue;
        this._handleInboundMessage(msg);
      }
    });

    // Handle pairing code if configured
    if (this.pairingMode === "pairing_code" && this.phoneNumber) {
      if (!this.sock.authState.creds.registered) {
        try {
          const code = await this.sock.requestPairingCode(this.phoneNumber);
          sendNotification("channel_auth_event", {
            channelId: this.channelId,
            state: "pairing_code",
            data: code,
            accountId: this.accountId,
          });
        } catch (err) {
          console.error(`Pairing code request failed: ${err?.message}`);
          sendNotification("channel_auth_event", {
            channelId: this.channelId,
            state: "error",
            data: `Pairing code request failed: ${err?.message}`,
            accountId: this.accountId,
          });
        }
      }
    }
  }

  async stop() {
    this._stopped = true;
    if (this.sock) {
      this.sock.end(undefined);
      this.sock = null;
    }
  }

  async send(request) {
    if (!this.sock) throw new Error("Session not connected");

    const jid = request.recipientId;
    const options = {};

    if (request.replyToMessageId) {
      options.quoted = {
        key: {
          remoteJid: jid,
          id: request.replyToMessageId,
        },
      };
    }

    // Handle attachments
    if (request.attachments && request.attachments.length > 0) {
      const att = request.attachments[0];
      const media = await prepareOutboundMedia(att);
      if (media) {
        const content = this._buildMediaContent(att.type, media, request.text);
        await this.sock.sendMessage(jid, content, options);
        return;
      }
    }

    // Text-only message
    await this.sock.sendMessage(jid, { text: request.text || "" }, options);
  }

  async sendTyping(recipientId, isTyping) {
    if (!this.sock) return;
    try {
      await this.sock.sendPresenceUpdate(
        isTyping ? "composing" : "paused",
        recipientId
      );
    } catch (err) {
      console.error(`Typing indicator failed: ${err?.message}`);
    }
  }

  async sendReadReceipt(messageId, remoteJid, participant) {
    if (!this.sock || !remoteJid) return;
    try {
      const keys = [{ remoteJid, id: messageId, participant: participant || undefined }];
      await this.sock.readMessages(keys);
    } catch (err) {
      console.error(`Read receipt failed: ${err?.message}`);
    }
  }

  async sendReaction(messageId, emoji, remoteJid, participant) {
    if (!this.sock || !remoteJid) return;
    try {
      await this.sock.sendMessage(remoteJid, {
        react: {
          text: emoji,
          key: {
            remoteJid,
            id: messageId,
            participant: participant || undefined,
          },
        },
      });
    } catch (err) {
      console.error(`Reaction failed: ${err?.message}`);
    }
  }

  _handleConnectionUpdate(update) {
    const { connection, lastDisconnect, qr } = update;

    if (qr && this.pairingMode !== "pairing_code") {
      sendNotification("channel_auth_event", {
        channelId: this.channelId,
        state: "qr_code",
        data: qr,
        accountId: this.accountId,
      });
    }

    if (connection === "open") {
      this.selfId = jidNormalizedUser(this.sock.user?.id || "");
      this._reconnectAttempt = 0;
      sendNotification("channel_auth_event", {
        channelId: this.channelId,
        state: "connected",
        data: this.selfId,
        accountId: this.accountId,
      });
    }

    if (connection === "close") {
      const statusCode =
        lastDisconnect?.error instanceof Boom
          ? lastDisconnect.error.output?.statusCode
          : 500;

      if (statusCode === DisconnectReason.loggedOut) {
        sendNotification("channel_auth_event", {
          channelId: this.channelId,
          state: "error",
          data: "Logged out. Re-pairing required.",
          accountId: this.accountId,
        });
        return;
      }

      if (!this._stopped) {
        sendNotification("channel_auth_event", {
          channelId: this.channelId,
          state: "disconnected",
          data: `Connection closed (${statusCode}). Reconnecting...`,
          accountId: this.accountId,
        });
        this._reconnect();
      }
    }
  }

  async _reconnect() {
    this._reconnectAttempt++;
    const delay = Math.min(1000 * Math.pow(2, this._reconnectAttempt - 1), 30000);
    console.error(
      `Reconnecting account ${this.accountId} in ${delay}ms (attempt ${this._reconnectAttempt})`
    );
    await new Promise((resolve) => setTimeout(resolve, delay));
    if (!this._stopped) {
      await this.start();
    }
  }

  async _handleInboundMessage(msg) {
    try {
      const mediaInfo = await downloadInboundMedia(msg, this.mediaCachePath);
      const mapped = mapInboundMessage(msg, mediaInfo, this.accountId);
      if (!mapped) return;

      sendNotification("channel_message", {
        channelId: this.channelId,
        ...mapped,
      });
    } catch (err) {
      console.error(`Failed to handle inbound message: ${err?.message}`);
    }
  }

  _buildMediaContent(type, media, caption) {
    switch (type) {
      case "image":
        return {
          image: media.buffer,
          caption: caption || media.caption || undefined,
          mimetype: media.mimeType,
        };
      case "video":
        return {
          video: media.buffer,
          caption: caption || media.caption || undefined,
          mimetype: media.mimeType,
          gifPlayback: media.gifPlayback || false,
        };
      case "audio":
        return {
          audio: media.buffer,
          mimetype: media.mimeType?.includes("ogg")
            ? "audio/ogg; codecs=opus"
            : media.mimeType,
          ptt: true,
        };
      case "document":
        return {
          document: media.buffer,
          mimetype: media.mimeType,
          fileName: media.fileName || "document",
        };
      case "sticker":
        return {
          sticker: media.buffer,
          mimetype: media.mimeType || "image/webp",
        };
      default:
        return {
          document: media.buffer,
          mimetype: media.mimeType,
          fileName: media.fileName || "file",
        };
    }
  }
}
