package main

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/http/httptest"
	"os"
	"os/exec"
	"strings"
	"sync/atomic"
	"testing"
	"time"

	"github.com/QuantumNous/new-api/relaykit/relayconvert"
	"github.com/QuantumNous/new-api/relaykit/relayconvert/convmeta"
	"github.com/QuantumNous/new-api/relaykit/types"
)

func TestActualCodexWithForeignModelCatalogAndMessagesGateway(t *testing.T) {
	executable := os.Getenv("MATHGPT_TEST_CODEX")
	catalogPath := os.Getenv("MATHGPT_TEST_CATALOG")
	if executable == "" || catalogPath == "" {
		t.Skip("optional real CLI integration; no paid provider calls")
	}
	b := fixture(t, "messages", func(w http.ResponseWriter, r *http.Request) {
		data, _ := io.ReadAll(r.Body)
		if !strings.Contains(string(data), `"model":"claude-opus-5-5"`) {
			t.Error("CLI did not preserve foreign model ID")
		}
		w.Header().Set("Content-Type", "text/event-stream")
		events := []string{`{"type":"message_start","message":{"id":"msg-fixture","type":"message","role":"assistant","model":"claude-opus-5-5","content":[],"usage":{"input_tokens":12,"output_tokens":0}}}`, `{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}`, `{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"FIXTURE_OK"}}`, `{"type":"content_block_stop","index":0}`, `{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":4}}`, `{"type":"message_stop"}`}
		for _, event := range events {
			_, _ = fmt.Fprintf(w, "data: %s\n\n", event)
		}
	})
	b.config.Models = map[string]string{"claude-opus-5-5": "messages"}
	listener, err := net.Listen("tcp4", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	b.config.Port = listener.Addr().(*net.TCPAddr).Port
	local := httptest.NewUnstartedServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		raw, _ := io.ReadAll(r.Body)
		if r.URL.Path == "/v1/responses" {
			if parsed, e := decodeRequest("responses", raw); e == nil {
				if _, e = relayconvert.ConvertRequest(r.Context(), &convmeta.Values{OriginModelName: "claude-opus-5-5", Options: &convmeta.Options{ToolLossPolicy: types.ConversionLossPolicySafe, Claude: convmeta.ClaudeOptions{DefaultMaxTokens: func(string) int { return 8192 }}}}, types.RelayFormatClaude, parsed); e != nil {
					t.Logf("fixture conversion diagnostic: %v", e)
				}
			}
		}
		r.Body = io.NopCloser(bytes.NewReader(raw))
		captured := httptest.NewRecorder()
		b.ServeHTTP(captured, r)
		for key, values := range captured.Header() {
			for _, value := range values {
				w.Header().Add(key, value)
			}
		}
		w.WriteHeader(captured.Code)
		_, _ = io.WriteString(w, captured.Body.String())
	}))
	local.Listener = listener
	local.Start()
	defer local.Close()
	root := t.TempDir()
	content, err := os.ReadFile(catalogPath)
	if err != nil {
		t.Fatal(err)
	}
	if err = os.WriteFile(root+"/models.json", content, 0600); err != nil {
		t.Fatal(err)
	}
	path, _ := json.Marshal(root + "/models.json")
	config := fmt.Sprintf("model = \"claude-opus-5-5\"\nmodel_provider = \"mathgpt\"\nmodel_catalog_json = %s\nweb_search = \"disabled\"\n[model_providers.mathgpt]\nname = \"MathGPT\"\nbase_url = \"%s/v1\"\nwire_api = \"responses\"\nenv_key = \"MATHGPT_TEST_LOCAL_KEY\"\nrequires_openai_auth = false\nsupports_websockets = false\nrequest_max_retries = 0\nstream_max_retries = 0\n", path, local.URL)
	if err = os.WriteFile(root+"/config.toml", []byte(config), 0600); err != nil {
		t.Fatal(err)
	}
	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	defer cancel()
	command := exec.CommandContext(ctx, executable, "exec", "--skip-git-repo-check", "--sandbox", "read-only", "--json", "--cd", root, "Return FIXTURE_OK only. Do not run tools.")
	command.Env = append(os.Environ(), "CODEX_HOME="+root, "MATHGPT_TEST_LOCAL_KEY="+fixtureToken, "NO_PROXY=127.0.0.1,localhost")
	output, err := command.CombinedOutput()
	if err != nil || !strings.Contains(string(output), "FIXTURE_OK") {
		t.Fatalf("native CLI adapter integration failed: %v; %s", err, output)
	}
	if strings.Contains(string(output), fixtureToken) || strings.Contains(string(output), fixtureKey) {
		t.Fatal("test output exposed a credential")
	}
}

const fixtureToken = "fixture-local-credential-never-forwarded"
const fixtureKey = "fixture-upstream-credential-never-returned"

func fixture(t *testing.T, upstream string, handler http.HandlerFunc) *bridge {
	t.Helper()
	server := httptest.NewTLSServer(handler)
	t.Cleanup(server.Close)
	b, err := newBridge(config{Port: 39001, Token: fixtureToken, Key: fixtureKey, BaseURL: server.URL + "/v1", Revision: "fixture-revision", Models: map[string]string{"real-model": upstream}}, server.Client())
	if err != nil {
		t.Fatal(err)
	}
	return b
}
func call(b *bridge, format, body string) *httptest.ResponseRecorder {
	request := httptest.NewRequest(http.MethodPost, "http://127.0.0.1:39001/v1"+pathFor(format), strings.NewReader(body))
	request.Header.Set("Authorization", "Bearer "+fixtureToken)
	recorder := httptest.NewRecorder()
	b.ServeHTTP(recorder, request)
	return recorder
}
func input(format string, stream bool) string {
	base := map[string]any{"model": "real-model", "stream": stream}
	schema := map[string]any{"type": "object", "properties": map[string]any{"x": map[string]string{"type": "integer"}}, "required": []string{"x"}}
	fn := map[string]any{"name": "read_probe", "description": "read a probe", "parameters": schema}
	switch format {
	case "responses":
		base["input"] = []any{map[string]string{"role": "user", "content": "call read_probe with x=7"}}
		base["max_output_tokens"] = 256
		base["tools"] = []any{map[string]any{"type": "function", "name": "read_probe", "parameters": schema}}
	case "messages":
		base["messages"] = []any{map[string]string{"role": "user", "content": "call read_probe with x=7"}}
		base["max_tokens"] = 256
		base["tools"] = []any{map[string]any{"name": "read_probe", "input_schema": schema}}
	default:
		base["messages"] = []any{map[string]string{"role": "user", "content": "call read_probe with x=7"}}
		base["max_tokens"] = 256
		base["tools"] = []any{map[string]any{"type": "function", "function": fn}}
	}
	data, _ := json.Marshal(base)
	return string(data)
}
func toolReply(format string) string {
	switch format {
	case "messages":
		return `{"id":"msg-probe","type":"message","role":"assistant","model":"real-model","content":[{"type":"tool_use","id":"call-probe","name":"read_probe","input":{"x":7}}],"stop_reason":"tool_use","usage":{"input_tokens":12,"output_tokens":8}}`
	case "responses":
		return `{"id":"resp-probe","object":"response","model":"real-model","status":"completed","output":[{"id":"fc-probe","type":"function_call","call_id":"call-probe","name":"read_probe","arguments":"{\"x\":7}","status":"completed"}],"usage":{"input_tokens":12,"output_tokens":8,"total_tokens":20}}`
	default:
		return `{"id":"chat-probe","object":"chat.completion","model":"real-model","choices":[{"index":0,"message":{"role":"assistant","content":null,"tool_calls":[{"id":"call-probe","type":"function","function":{"name":"read_probe","arguments":"{\"x\":7}"}}]},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":12,"completion_tokens":8,"total_tokens":20}}`
	}
}
func TestAllProtocolPairsPreserveRealModelAndNativeTools(t *testing.T) {
	for _, from := range []string{"responses", "messages", "chat"} {
		for _, to := range []string{"responses", "messages", "chat"} {
			t.Run(from+"_to_"+to, func(t *testing.T) {
				var calls atomic.Int32
				b := fixture(t, to, func(w http.ResponseWriter, r *http.Request) {
					calls.Add(1)
					if r.URL.Path != "/v1"+pathFor(to) {
						t.Errorf("wrong upstream path: %s", r.URL.Path)
					}
					if r.Header.Get("Authorization") != "Bearer "+fixtureKey || r.Header.Get("Authorization") == "Bearer "+fixtureToken {
						t.Error("incorrect credential boundary")
					}
					raw, _ := io.ReadAll(r.Body)
					if !strings.Contains(string(raw), `"model":"real-model"`) || !strings.Contains(string(raw), "read_probe") {
						t.Errorf("lost model or tools: %s", raw)
					}
					w.Header().Set("Content-Type", "application/json")
					_, _ = io.WriteString(w, toolReply(to))
				})
				result := call(b, from, input(from, false))
				if result.Code != 200 {
					t.Fatalf("HTTP %d: %s", result.Code, result.Body.String())
				}
				if calls.Load() != 1 {
					t.Fatal("automatic retry occurred")
				}
				if !strings.Contains(result.Body.String(), "read_probe") || !strings.Contains(result.Body.String(), "call-probe") {
					t.Fatal("native tool semantics were lost")
				}
				if strings.Contains(result.Body.String(), fixtureKey) {
					t.Fatal("credential leak")
				}
			})
		}
	}
}

func TestOfficialModelReasoningAcrossProtocols(t *testing.T) {
	for _, model := range []string{"claude-opus-5-5", "gemini-4-argon"} {
		pairs := [][2]string{{"responses", "messages"}, {"chat", "messages"}, {"messages", "messages"}}
		if model == "gemini-4-argon" {
			pairs = [][2]string{{"responses", "chat"}, {"messages", "chat"}, {"chat", "chat"}}
		}
		for _, pair := range pairs {
			for _, effort := range []string{"low", "medium", "high"} {
				t.Run(model+"/"+pair[0]+"/"+effort, func(t *testing.T) {
					var actual map[string]any
					b := fixture(t, pair[1], func(w http.ResponseWriter, r *http.Request) {
						json.NewDecoder(r.Body).Decode(&actual)
						w.Header().Set("Content-Type", "application/json")
						io.WriteString(w, toolReply(pair[1]))
					})
					b.config.Models = map[string]string{model: pair[1]}
					var body map[string]any
					json.Unmarshal([]byte(input(pair[0], false)), &body)
					body["model"] = model
					switch pair[0] {
					case "responses":
						body["reasoning"] = map[string]any{"effort": effort}
					case "messages":
						body["output_config"] = map[string]any{"effort": effort}
					default:
						body["reasoning_effort"] = effort
					}
					encoded, _ := json.Marshal(body)
					reply := call(b, pair[0], string(encoded))
					if reply.Code != 200 {
						t.Fatalf("HTTP %d: %s", reply.Code, reply.Body.String())
					}
					if actual["model"] != model {
						t.Fatal("model changed")
					}
					var got any
					switch pair[1] {
					case "responses":
						if x, ok := actual["reasoning"].(map[string]any); ok {
							got = x["effort"]
						}
					case "messages":
						if x, ok := actual["output_config"].(map[string]any); ok {
							got = x["effort"]
						}
					default:
						got = actual["reasoning_effort"]
					}
					if got != effort {
						t.Fatalf("effort changed: %v expected %s", got, effort)
					}
				})
			}
		}
	}
}
func TestLocalSecurityAndModelLimits(t *testing.T) {
	var calls atomic.Int32
	b := fixture(t, "messages", func(w http.ResponseWriter, r *http.Request) { calls.Add(1) })
	for _, variant := range []string{"wrong_key", "browser_origin", "rebinding_host", "unknown_model", "previous_response"} {
		t.Run(variant, func(t *testing.T) {
			body := input("responses", false)
			if variant == "unknown_model" {
				body = strings.ReplaceAll(body, "real-model", "other-model")
			}
			if variant == "previous_response" {
				body = strings.TrimSuffix(body, "}") + `,"previous_response_id":"resp-old"}`
			}
			r := httptest.NewRequest(http.MethodPost, "http://127.0.0.1:39001/v1/responses", strings.NewReader(body))
			r.Header.Set("Authorization", "Bearer "+fixtureToken)
			switch variant {
			case "wrong_key":
				r.Header.Set("Authorization", "Bearer wrong")
			case "browser_origin":
				r.Header.Set("Origin", "https://attacker.invalid")
			case "rebinding_host":
				r.Host = "attacker.invalid:39001"
			}
			w := httptest.NewRecorder()
			b.ServeHTTP(w, r)
			if w.Code < 400 {
				t.Fatalf("unsafe request accepted: %s", variant)
			}
		})
	}
	if calls.Load() != 0 {
		t.Fatal("denied request reached gateway")
	}
}
func TestMessagesStreamToResponsesPreservesToolArgumentsAndTerminal(t *testing.T) {
	b := fixture(t, "messages", func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "text/event-stream")
		events := []string{
			`{"type":"message_start","message":{"id":"msg-probe","type":"message","role":"assistant","model":"real-model","content":[],"usage":{"input_tokens":12,"output_tokens":0}}}`,
			`{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"call-probe","name":"read_probe","input":{}}}`,
			`{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"x\":7}"}}`,
			`{"type":"content_block_stop","index":0}`, `{"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":8}}`, `{"type":"message_stop"}`,
		}
		for _, e := range events {
			_, _ = fmt.Fprintf(w, "data: %s\n\n", e)
		}
	})
	result := call(b, "responses", input("responses", true))
	text := result.Body.String()
	if strings.Contains(text, `"Payload"`) || strings.Contains(text, `"Type"`) {
		t.Fatal("Internal event envelope leaked onto the SSE wire")
	}
	for _, expected := range []string{"read_probe", "call-probe", "response.completed", "function_call"} {
		if !strings.Contains(text, expected) {
			t.Errorf("missing %s from stream: %s", expected, text)
		}
	}
}
func TestPrematureStreamIsNotReportedAsSuccess(t *testing.T) {
	b := fixture(t, "messages", func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "text/event-stream")
		_, _ = io.WriteString(w, "data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg-probe\",\"model\":\"real-model\",\"role\":\"assistant\",\"content\":[]}}\n\n")
	})
	result := call(b, "responses", input("responses", true))
	if strings.Contains(result.Body.String(), "response.completed") || !strings.Contains(result.Body.String(), "incomplete_stream") {
		t.Fatalf("truncated stream misreported: %s", result.Body.String())
	}
}
