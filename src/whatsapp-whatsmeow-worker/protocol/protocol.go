package protocol

import (
	"encoding/json"
	"io"
	"sync"
)

// BridgeRequest is the JSON-RPC request from the gateway.
type BridgeRequest struct {
	Method string           `json:"method"`
	ID     string           `json:"id"`
	Params *json.RawMessage `json:"params,omitempty"`
}

// BridgeResponse is the JSON-RPC response to the gateway.
type BridgeResponse struct {
	ID     string       `json:"id"`
	Result interface{}  `json:"result,omitempty"`
	Error  *BridgeError `json:"error,omitempty"`
}

// BridgeError is an error payload in a response.
type BridgeError struct {
	Code    int    `json:"code"`
	Message string `json:"message"`
}

// BridgeNotification is an unsolicited event from worker to gateway.
type BridgeNotification struct {
	Notification string      `json:"notification"`
	Params       interface{} `json:"params"`
}

// Writer handles thread-safe JSON line output to stdout.
type Writer struct {
	mu  sync.Mutex
	enc *json.Encoder
}

// NewWriter creates a new thread-safe JSON-RPC writer.
func NewWriter(w io.Writer) *Writer {
	enc := json.NewEncoder(w)
	enc.SetEscapeHTML(false)
	return &Writer{enc: enc}
}

// SendResponse writes a success response.
func (w *Writer) SendResponse(id string, result interface{}) {
	w.mu.Lock()
	defer w.mu.Unlock()
	_ = w.enc.Encode(BridgeResponse{ID: id, Result: result})
}

// SendError writes an error response.
func (w *Writer) SendError(id string, code int, message string) {
	w.mu.Lock()
	defer w.mu.Unlock()
	_ = w.enc.Encode(BridgeResponse{
		ID:    id,
		Error: &BridgeError{Code: code, Message: message},
	})
}

// SendNotification writes an unsolicited notification.
func (w *Writer) SendNotification(notificationType string, params interface{}) {
	w.mu.Lock()
	defer w.mu.Unlock()
	_ = w.enc.Encode(BridgeNotification{
		Notification: notificationType,
		Params:       params,
	})
}
