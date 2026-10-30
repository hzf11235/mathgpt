// MathGPT protocol bridge. SPDX-License-Identifier: AGPL-3.0-or-later.
// Protocol conversions are provided by QuantumNous/new-api relaykit.
package main

import (
	"bufio"
	"bytes"
	"context"
	"crypto/subtle"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/url"
	"os"
	"strings"
	"sync/atomic"
	"time"

	"github.com/QuantumNous/new-api/relaykit/dto"
	"github.com/QuantumNous/new-api/relaykit/relayconvert"
	"github.com/QuantumNous/new-api/relaykit/relayconvert/convmeta"
	"github.com/QuantumNous/new-api/relaykit/types"
)

const maxBody = 16 << 20

type config struct {
	Port           int               `json:"port"`
	Token          string            `json:"token"`
	Key            string            `json:"key"`
	BaseURL        string            `json:"baseUrl"`
	Revision       string            `json:"revision"`
	Models         map[string]string `json:"models"`
	DefaultEfforts map[string]string `json:"defaultEfforts"`
}
type bridge struct {
	config   config
	client   *http.Client
	last     atomic.Int64
	active   atomic.Int32
	shutdown func()
}

func newBridge(c config, client *http.Client) (*bridge, error) {
	u, err := url.Parse(c.BaseURL)
	if err != nil || u.Scheme != "https" || u.Host == "" || u.User != nil || u.RawQuery != "" || u.Fragment != "" || len(c.Token) < 32 || len(c.Key) < 8 || c.Port < 1024 || c.Port > 65535 || len(c.Models) == 0 {
		return nil, errors.New("invalid bridge configuration")
	}
	for model, format := range c.Models {
		if model == "" || strings.ContainsAny(model, "\r\n") || !validFormat(format) {
			return nil, errors.New("invalid model configuration")
		}
	}
	b := &bridge{config: c, client: client}
	b.last.Store(time.Now().Unix())
	return b, nil
}
func validFormat(v string) bool { return v == "responses" || v == "messages" || v == "chat" }
func relayFormat(v string) types.RelayFormat {
	if v == "responses" {
		return types.RelayFormatOpenAIResponses
	}
	if v == "messages" {
		return types.RelayFormatClaude
	}
	return types.RelayFormatOpenAI
}
func pathFor(v string) string {
	if v == "responses" {
		return "/responses"
	}
	if v == "messages" {
		return "/messages"
	}
	return "/chat/completions"
}
func (b *bridge) ServeHTTP(w http.ResponseWriter, r *http.Request) {
	// The browser is not a client of this private service. Reject web origins,
	// rebinding hosts and requests without an independent per-profile secret.
	expected := fmt.Sprintf("127.0.0.1:%d", b.config.Port)
	if r.Host != expected || r.Header.Get("Origin") != "" || r.Header.Get("Sec-Fetch-Site") == "cross-site" {
		fail(w, 403, "local_access_denied")
		return
	}
	token := strings.TrimPrefix(r.Header.Get("Authorization"), "Bearer ")
	if token == "" {
		token = r.Header.Get("x-api-key")
	}
	if subtle.ConstantTimeCompare([]byte(token), []byte(b.config.Token)) != 1 {
		fail(w, 401, "invalid_local_credential")
		return
	}
	b.last.Store(time.Now().Unix())
	if r.URL.Path == "/health" && r.Method == http.MethodGet {
		writeJSON(w, map[string]any{"revision": b.config.Revision, "service": "MathGPT.Bridge", "version": 1})
		return
	}
	if r.URL.Path == "/shutdown" && r.Method == http.MethodPost {
		if b.active.Load() > 0 {
			fail(w, 409, "requests_in_progress")
			return
		}
		writeJSON(w, map[string]bool{"ok": true})
		if b.shutdown != nil {
			go b.shutdown()
		}
		return
	}
	if r.URL.Path == "/v1/models" && r.Method == http.MethodGet {
		data := []any{}
		for model := range b.config.Models {
			data = append(data, map[string]string{"id": model, "object": "model"})
		}
		writeJSON(w, map[string]any{"object": "list", "data": data})
		return
	}
	var incoming string
	switch r.URL.Path {
	case "/v1/responses":
		incoming = "responses"
	case "/v1/messages":
		incoming = "messages"
	case "/v1/chat/completions":
		incoming = "chat"
	default:
		fail(w, 404, "unsupported_local_endpoint")
		return
	}
	if r.Method != http.MethodPost {
		fail(w, 405, "method_not_allowed")
		return
	}
	b.active.Add(1)
	defer b.active.Add(-1)
	body, err := io.ReadAll(http.MaxBytesReader(w, r.Body, maxBody))
	if err != nil {
		fail(w, 413, "request_too_large")
		return
	}
	var fields map[string]json.RawMessage
	if json.Unmarshal(body, &fields) != nil {
		fail(w, 400, "invalid_json")
		return
	}
	var model string
	_ = json.Unmarshal(fields["model"], &model)
	upstream, ok := b.config.Models[model]
	if !ok {
		fail(w, 400, "model_not_in_current_key_catalog")
		return
	}
	var stream bool
	_ = json.Unmarshal(fields["stream"], &stream)
	// A client's explicit effort wins; the connection supplies only a default.
	if effort := b.config.DefaultEfforts[model]; effort != "" {
		switch incoming {
		case "responses":
			var reasoning map[string]any
			_ = json.Unmarshal(fields["reasoning"], &reasoning)
			if reasoning == nil {
				reasoning = map[string]any{}
			}
			if reasoning["effort"] == nil {
				reasoning["effort"] = effort
				fields["reasoning"], _ = json.Marshal(reasoning)
			}
		case "messages":
			var output map[string]any
			_ = json.Unmarshal(fields["output_config"], &output)
			if output == nil {
				output = map[string]any{}
			}
			if output["effort"] == nil {
				output["effort"] = effort
				fields["output_config"], _ = json.Marshal(output)
			}
		default:
			if fields["reasoning_effort"] == nil {
				fields["reasoning_effort"], _ = json.Marshal(effort)
			}
		}
		body, _ = json.Marshal(fields)
	}
	meta := &convmeta.Values{OriginModelName: model, UpstreamModelName: model, IsStream: stream, Options: &convmeta.Options{ToolLossPolicy: types.ConversionLossPolicySafe, Claude: convmeta.ClaudeOptions{DefaultMaxTokens: func(string) int { return 8192 }}}}
	if incoming != upstream {
		typed, e := decodeRequest(incoming, body)
		if e != nil {
			fail(w, 400, "invalid_protocol_request")
			return
		}
		converted, e := relayconvert.ConvertRequest(r.Context(), meta, relayFormat(upstream), typed)
		if e != nil {
			code := "unsupported_protocol_feature"
			var loss *types.ConversionLossError
			if errors.As(e, &loss) && len(loss.Diagnostics) > 0 {
				code += ": " + loss.Diagnostics[0].Code
			}
			fail(w, 400, code)
			return
		}
		body, e = json.Marshal(converted.Value)
		if e != nil {
			fail(w, 400, "request_conversion_failed")
			return
		}
	}
	// Model identity is never replaced with a GPT/Claude alias. The exact model
	// chosen by the user must survive every conversion.
	var convertedFields map[string]json.RawMessage
	if json.Unmarshal(body, &convertedFields) != nil {
		fail(w, 400, "request_conversion_failed")
		return
	}
	convertedFields["model"], _ = json.Marshal(model)
	convertedFields["stream"], _ = json.Marshal(stream)
	delete(convertedFields, "store")
	delete(convertedFields, "previous_response_id")
	if fields["previous_response_id"] != nil && string(fields["previous_response_id"]) != "null" {
		fail(w, 400, "send_explicit_conversation_history")
		return
	}
	if upstream == "messages" && convertedFields["tools"] != nil && (strings.Contains(model, "opus-5-5") || strings.Contains(model, "sonnet-5-5")) {
		convertedFields["tool_choice"] = json.RawMessage(`{"type":"auto"}`)
	}
	body, _ = json.Marshal(convertedFields)
	request, err := http.NewRequestWithContext(r.Context(), http.MethodPost, strings.TrimRight(b.config.BaseURL, "/")+pathFor(upstream), bytes.NewReader(body))
	if err != nil {
		fail(w, 400, "invalid_gateway_address")
		return
	}
	request.Header.Set("Content-Type", "application/json")
	request.Header.Set("Authorization", "Bearer "+b.config.Key)
	if upstream == "messages" {
		request.Header.Set("x-api-key", b.config.Key)
		request.Header.Set("anthropic-version", "2023-06-01")
	}
	if stream {
		request.Header.Set("Accept", "text/event-stream")
	}
	response, err := b.client.Do(request)
	if err != nil {
		fail(w, 502, "gateway_request_failed")
		return
	}
	defer response.Body.Close()
	if response.StatusCode < 200 || response.StatusCode >= 300 {
		status := response.StatusCode
		if status >= 300 && status < 400 {
			status = 502
		}
		fail(w, status, "gateway_rejected_request")
		return
	}
	if id := response.Header.Get("X-Request-Id"); id != "" {
		w.Header().Set("X-Request-Id", id)
	}
	if incoming == upstream {
		w.Header().Set("Content-Type", response.Header.Get("Content-Type"))
		w.Header().Set("Cache-Control", "no-store")
		if stream {
			copyStream(w, response.Body)
		} else {
			copyLimited(w, response.Body)
		}
		return
	}
	if stream {
		b.stream(w, r, response, upstream, incoming, model, meta)
		return
	}
	raw, err := io.ReadAll(io.LimitReader(response.Body, maxBody+1))
	if err != nil || len(raw) > maxBody {
		fail(w, 502, "invalid_gateway_response")
		return
	}
	typed, err := decodeResponse(upstream, raw, false)
	if err != nil {
		fail(w, 502, "invalid_gateway_response")
		return
	}
	result, err := relayconvert.ConvertResponse(r.Context(), meta, relayFormat(incoming), typed)
	if err != nil {
		fail(w, 502, "response_conversion_failed")
		return
	}
	writeJSON(w, result.Value)
}
func decodeRequest(format string, data []byte) (any, error) {
	var v any
	switch format {
	case "responses":
		v = &dto.OpenAIResponsesRequest{}
	case "messages":
		v = &dto.ClaudeRequest{}
	default:
		v = &dto.GeneralOpenAIRequest{}
	}
	return v, json.Unmarshal(data, v)
}
func decodeResponse(format string, data []byte, stream bool) (any, error) {
	var v any
	switch format {
	case "responses":
		if stream {
			v = &dto.ResponsesStreamResponse{}
		} else {
			v = &dto.OpenAIResponsesResponse{}
		}
	case "messages":
		v = &dto.ClaudeResponse{}
	default:
		if stream {
			v = &dto.ChatCompletionsStreamResponse{}
		} else {
			v = &dto.OpenAITextResponse{}
		}
	}
	return v, json.Unmarshal(data, v)
}
func (b *bridge) stream(w http.ResponseWriter, r *http.Request, response *http.Response, from, to, model string, meta *convmeta.Values) {
	if !strings.Contains(response.Header.Get("Content-Type"), "text/event-stream") {
		fail(w, 502, "gateway_did_not_return_stream")
		return
	}
	state, err := relayconvert.NewResponseStreamState(relayFormat(from), relayFormat(to), relayconvert.ResponseStreamOptions{Model: model, Created: time.Now().Unix(), IncludeUsage: true, EmitSequenceNumber: true})
	if err != nil {
		fail(w, 502, "stream_conversion_unavailable")
		return
	}
	w.Header().Set("Content-Type", "text/event-stream")
	w.Header().Set("Cache-Control", "no-store")
	w.WriteHeader(200)
	emit := func(results []relayconvert.ResponseResult) bool {
		for _, result := range results {
			if !sendEvent(w, to, result.Value) {
				return false
			}
		}
		return true
	}
	scanner := bufio.NewScanner(response.Body)
	scanner.Buffer(make([]byte, 4096), maxBody)
	var payload strings.Builder
	terminal := false
	consume := func() bool {
		data := strings.TrimSpace(payload.String())
		payload.Reset()
		if data == "" {
			return true
		}
		if data == "[DONE]" {
			terminal = true
			return true
		}
		var fields map[string]json.RawMessage
		if json.Unmarshal([]byte(data), &fields) != nil {
			return false
		}
		var typ string
		_ = json.Unmarshal(fields["type"], &typ)
		if typ == "error" || typ == "response.failed" || typ == "response.incomplete" {
			sendEvent(w, to, map[string]any{"type": "error", "error": map[string]string{"type": "gateway_stream_error", "message": "The gateway did not complete the request."}})
			return false
		}
		if typ == "response.completed" || typ == "message_stop" {
			terminal = true
		}
		if from == "chat" {
			var chat struct {
				Choices []struct {
					FinishReason *string `json:"finish_reason"`
				} `json:"choices"`
			}
			_ = json.Unmarshal([]byte(data), &chat)
			for _, choice := range chat.Choices {
				if choice.FinishReason != nil && *choice.FinishReason != "" {
					terminal = true
				}
			}
		}
		typed, e := decodeResponse(from, []byte(data), true)
		if e != nil {
			return false
		}
		results, e := relayconvert.ConvertStreamResponseChunk(r.Context(), meta, state, typed)
		return e == nil && emit(results)
	}
	for scanner.Scan() {
		line := scanner.Text()
		if line == "" {
			if !consume() {
				return
			}
		} else if strings.HasPrefix(line, "data:") {
			if payload.Len() > 0 {
				payload.WriteByte('\n')
			}
			payload.WriteString(strings.TrimSpace(strings.TrimPrefix(line, "data:")))
			if payload.Len() > maxBody {
				return
			}
		}
	}
	if payload.Len() > 0 && !consume() {
		return
	}
	if scanner.Err() != nil || r.Context().Err() != nil {
		return
	}
	if !terminal {
		sendEvent(w, to, map[string]any{"type": "error", "error": map[string]string{"type": "incomplete_stream", "message": "The gateway stream ended before completion."}})
		return
	}
	tail, err := relayconvert.FinalizeStreamResponse(r.Context(), meta, state)
	if err != nil || !emit(tail) {
		return
	}
	if to == "chat" {
		_, _ = io.WriteString(w, "data: [DONE]\n\n")
		flush(w)
	}
}
func sendEvent(w http.ResponseWriter, format string, value any) bool {
	switch event := value.(type) {
	case relayconvert.ChatToResponsesStreamEvent:
		value = event.Payload
	case *relayconvert.ChatToResponsesStreamEvent:
		value = event.Payload
	}
	data, err := json.Marshal(value)
	if err != nil {
		return false
	}
	var fields map[string]json.RawMessage
	_ = json.Unmarshal(data, &fields)
	var typ string
	_ = json.Unmarshal(fields["type"], &typ)
	if format != "chat" && typ != "" {
		if _, err = fmt.Fprintf(w, "event: %s\n", typ); err != nil {
			return false
		}
	}
	_, err = fmt.Fprintf(w, "data: %s\n\n", data)
	flush(w)
	return err == nil
}
func flush(w http.ResponseWriter) {
	if f, ok := w.(http.Flusher); ok {
		f.Flush()
	}
}
func copyStream(w http.ResponseWriter, r io.Reader) {
	buffer := make([]byte, 16<<10)
	for {
		n, err := r.Read(buffer)
		if n > 0 {
			if _, e := w.Write(buffer[:n]); e != nil {
				return
			}
			flush(w)
		}
		if err != nil {
			return
		}
	}
}
func copyLimited(w http.ResponseWriter, r io.Reader) {
	data, err := io.ReadAll(io.LimitReader(r, maxBody+1))
	if err != nil || len(data) > maxBody {
		return
	}
	_, _ = w.Write(data)
}
func writeJSON(w http.ResponseWriter, value any) {
	w.Header().Set("Content-Type", "application/json")
	w.Header().Set("Cache-Control", "no-store")
	_ = json.NewEncoder(w).Encode(value)
}
func fail(w http.ResponseWriter, status int, code string) {
	w.Header().Set("Content-Type", "application/json")
	w.Header().Set("Cache-Control", "no-store")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(map[string]any{"type": "error", "error": map[string]string{"type": code, "message": "MathGPT local adapter: " + code}})
}
func main() {
	var c config
	if json.NewDecoder(io.LimitReader(os.Stdin, 1<<20)).Decode(&c) != nil {
		os.Exit(2)
	}
	transport := http.DefaultTransport.(*http.Transport).Clone()
	transport.ResponseHeaderTimeout = 90 * time.Second
	client := &http.Client{Transport: transport, CheckRedirect: func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse }}
	b, err := newBridge(c, client)
	if err != nil {
		os.Exit(2)
	}
	listener, err := net.Listen("tcp4", fmt.Sprintf("127.0.0.1:%d", c.Port))
	if err != nil {
		os.Exit(3)
	}
	server := &http.Server{Handler: b, ReadHeaderTimeout: 10 * time.Second, IdleTimeout: 90 * time.Second, MaxHeaderBytes: 32 << 10}
	b.shutdown = func() {
		ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
		defer cancel()
		_ = server.Shutdown(ctx)
	}
	go func() {
		ticker := time.NewTicker(time.Minute)
		defer ticker.Stop()
		for range ticker.C {
			if b.active.Load() == 0 && time.Now().Unix()-b.last.Load() > 900 {
				b.shutdown()
				return
			}
		}
	}()
	_ = json.NewEncoder(os.Stdout).Encode(map[string]any{"ready": true, "port": c.Port, "revision": c.Revision})
	_ = server.Serve(listener)
}
