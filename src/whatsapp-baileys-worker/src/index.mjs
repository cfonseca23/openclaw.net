// Redirect console.log to stderr so stdout stays clean for JSON-RPC
console.log = console.error;

import { readRequests, sendNotification } from "./protocol.mjs";
import { BaileysEngine } from "./engine.mjs";

const engine = new BaileysEngine();

async function handleRequest(request) {
  const { method, params } = request;

  switch (method) {
    case "init": {
      const config = params?.config ?? {};
      engine.init(config);
      return {
        channels: [{ id: "whatsapp" }],
        tools: [],
        commands: [],
        eventSubscriptions: [],
        providers: [],
        capabilities: ["channels"],
        diagnostics: [],
        compatible: true,
      };
    }

    case "channel_start": {
      return await engine.start();
    }

    case "channel_stop": {
      return await engine.stop();
    }

    case "channel_send": {
      return await engine.send(params);
    }

    case "channel_typing": {
      return await engine.sendTyping(params);
    }

    case "channel_read_receipt": {
      return await engine.sendReadReceipt(params);
    }

    case "channel_react": {
      return await engine.sendReaction(params);
    }

    case "debug_get_state": {
      return engine.getState();
    }

    case "shutdown": {
      await engine.stop();
      return { shutdown: true };
    }

    default:
      throw new Error(`Unknown method: ${method}`);
  }
}

// Start the stdio JSON-RPC loop
readRequests(handleRequest);
