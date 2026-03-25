import { downloadMediaMessage } from "@whiskeysockets/baileys";
import { writeFile, mkdir, readFile } from "fs/promises";
import { createWriteStream } from "fs";
import { join, extname } from "path";
import { pipeline } from "stream/promises";
import { Readable } from "stream";

const MIME_EXTENSIONS = {
  "image/jpeg": ".jpg",
  "image/png": ".png",
  "image/webp": ".webp",
  "image/gif": ".gif",
  "video/mp4": ".mp4",
  "audio/ogg": ".ogg",
  "audio/ogg; codecs=opus": ".ogg",
  "audio/mpeg": ".mp3",
  "audio/mp4": ".m4a",
  "application/pdf": ".pdf",
  "application/vnd.openxmlformats-officedocument.wordprocessingml.document":
    ".docx",
};

function getExtension(mimeType, fileName) {
  if (fileName) {
    const ext = extname(fileName);
    if (ext) return ext;
  }
  return MIME_EXTENSIONS[mimeType] || ".bin";
}

function detectMediaType(msg) {
  if (msg.imageMessage) return { type: "image", inner: msg.imageMessage };
  if (msg.videoMessage) return { type: "video", inner: msg.videoMessage };
  if (msg.audioMessage) return { type: "audio", inner: msg.audioMessage };
  if (msg.documentMessage)
    return { type: "document", inner: msg.documentMessage };
  if (msg.stickerMessage) return { type: "sticker", inner: msg.stickerMessage };
  return null;
}

export async function downloadInboundMedia(msg, cachePath) {
  const raw = msg.message;
  if (!raw) return null;

  const media = detectMediaType(raw);
  if (!media) return null;

  try {
    const buffer = await downloadMediaMessage(msg, "buffer", {});
    const mimeType = media.inner.mimetype || "application/octet-stream";
    const fileName = media.inner.fileName || null;
    const ext = getExtension(mimeType, fileName);
    const id = msg.key?.id || Date.now().toString();
    const outName = `${id}${ext}`;

    await mkdir(cachePath, { recursive: true });
    const outPath = join(cachePath, outName);
    await writeFile(outPath, buffer);

    return {
      mediaType: media.type,
      mediaUrl: `file://${outPath}`,
      mediaMimeType: mimeType,
      mediaFileName: fileName || outName,
    };
  } catch (err) {
    console.error(`Failed to download media: ${err?.message}`);
    return {
      mediaType: media.type,
      mediaUrl: null,
      mediaMimeType: media.inner?.mimetype || null,
      mediaFileName: media.inner?.fileName || null,
    };
  }
}

export async function prepareOutboundMedia(attachment) {
  if (!attachment?.url) return null;

  let buffer;
  if (
    attachment.url.startsWith("http://") ||
    attachment.url.startsWith("https://")
  ) {
    const response = await fetch(attachment.url);
    if (!response.ok)
      throw new Error(`Failed to fetch media: ${response.status}`);
    buffer = Buffer.from(await response.arrayBuffer());
  } else if (attachment.url.startsWith("file://")) {
    buffer = await readFile(attachment.url.slice(7));
  } else {
    buffer = await readFile(attachment.url);
  }

  return {
    buffer,
    mimeType: attachment.mimeType || "application/octet-stream",
    fileName: attachment.fileName || null,
    caption: attachment.caption || null,
    gifPlayback: attachment.gifPlayback || false,
  };
}
