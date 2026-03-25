package main

import (
	"bufio"
	"context"
	"encoding/json"
	"fmt"
	"os"
	"strings"

	"github.com/openclaw/whatsapp-whatsmeow-worker/engine"
	"github.com/openclaw/whatsapp-whatsmeow-worker/protocol"
)

func main() {
	hasStdio := false
	for _, arg := range os.Args[1:] {
		if arg == "--stdio" {
			hasStdio = true
			break
		}
	}
	if !hasStdio {
		fmt.Fprintln(os.Stderr, "Usage: whatsapp-whatsmeow-worker --stdio")
		os.Exit(2)
	}

	writer := protocol.NewWriter(os.Stdout)
	eng := engine.NewEngine(writer)
	ctx := context.Background()

	scanner := bufio.NewScanner(os.Stdin)
	scanner.Buffer(make([]byte, 0, 1024*1024), 1024*1024)

	for scanner.Scan() {
		line := strings.TrimSpace(scanner.Text())
		if line == "" {
			continue
		}
		if line == "__shutdown__" {
			break
		}

		var req protocol.BridgeRequest
		if err := json.Unmarshal([]byte(line), &req); err != nil {
			writer.SendError("unknown", -32700, "Parse error: "+err.Error())
			continue
		}

		result, err := handleRequest(ctx, eng, &req)
		if err != nil {
			writer.SendError(req.ID, -1, err.Error())
		} else {
			writer.SendResponse(req.ID, result)
		}

		if req.Method == "shutdown" {
			break
		}
	}

	eng.Stop()
}

func handleRequest(ctx context.Context, eng *engine.Engine, req *protocol.BridgeRequest) (interface{}, error) {
	switch req.Method {
	case "init":
		config, err := engine.ParseJSON[engine.Config](req.Params)
		if err != nil {
			return nil, fmt.Errorf("invalid config: %w", err)
		}
		eng.Init(config)
		return map[string]interface{}{
			"channels":           []map[string]string{{"id": "whatsapp"}},
			"tools":              []interface{}{},
			"commands":           []interface{}{},
			"eventSubscriptions": []interface{}{},
			"providers":          []interface{}{},
			"capabilities":       []string{"channels"},
			"diagnostics":        []interface{}{},
			"compatible":         true,
		}, nil

	case "channel_start":
		return eng.Start(ctx)

	case "channel_stop":
		eng.Stop()
		return map[string]interface{}{"stopped": true}, nil

	case "channel_send":
		sendReq, err := engine.ParseJSON[engine.SendRequest](req.Params)
		if err != nil {
			return nil, err
		}
		if err := eng.Send(ctx, sendReq); err != nil {
			return nil, err
		}
		return map[string]interface{}{"sent": true}, nil

	case "channel_typing":
		typingReq, err := engine.ParseJSON[engine.TypingRequest](req.Params)
		if err != nil {
			return nil, err
		}
		if err := eng.SendTyping(ctx, typingReq); err != nil {
			return nil, err
		}
		return map[string]interface{}{"accepted": true}, nil

	case "channel_read_receipt":
		receiptReq, err := engine.ParseJSON[engine.ReceiptRequest](req.Params)
		if err != nil {
			return nil, err
		}
		if err := eng.SendReadReceipt(ctx, receiptReq); err != nil {
			return nil, err
		}
		return map[string]interface{}{"accepted": true}, nil

	case "channel_react":
		reactReq, err := engine.ParseJSON[engine.ReactionRequest](req.Params)
		if err != nil {
			return nil, err
		}
		if err := eng.SendReaction(ctx, reactReq); err != nil {
			return nil, err
		}
		return map[string]interface{}{"accepted": true}, nil

	case "debug_get_state":
		return eng.GetState(), nil

	case "shutdown":
		eng.Stop()
		return map[string]interface{}{"shutdown": true}, nil

	default:
		return nil, fmt.Errorf("unknown method: %s", req.Method)
	}
}
