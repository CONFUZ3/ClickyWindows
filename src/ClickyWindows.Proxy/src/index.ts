export interface Env {
  ANTHROPIC_API_KEY: string;
  ELEVENLABS_API_KEY: string;
  // Note: AssemblyAI connects directly from the app, not via this proxy.
}

// CORS is permissive for localhost dev. For production Cloudflare deployment,
// restrict this to your specific worker domain.
function corsHeaders(): Record<string, string> {
  return {
    "Access-Control-Allow-Origin": "*",
    "Access-Control-Allow-Methods": "POST, OPTIONS",
    "Access-Control-Allow-Headers": "Content-Type",
  };
}

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    if (request.method === "OPTIONS") {
      return new Response(null, { headers: corsHeaders() });
    }

    const url = new URL(request.url);

    if (request.method === "POST" && url.pathname === "/chat") {
      return handleChat(request, env);
    }

    if (request.method === "POST" && url.pathname === "/tts") {
      return handleTts(request, env);
    }

    return new Response("Not Found", { status: 404 });
  },
};

async function handleChat(request: Request, env: Env): Promise<Response> {
  const body = await request.json() as Record<string, unknown>;

  const response = await fetch("https://api.anthropic.com/v1/messages", {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "x-api-key": env.ANTHROPIC_API_KEY,
      "anthropic-version": "2023-06-01",
    },
    body: JSON.stringify(body),
  });

  // Stream the SSE response back
  return new Response(response.body, {
    status: response.status,
    headers: {
      ...corsHeaders(),
      "Content-Type": response.headers.get("Content-Type") || "text/event-stream",
      "Cache-Control": "no-cache",
    },
  });
}

async function handleTts(request: Request, env: Env): Promise<Response> {
  const body = await request.json() as {
    text: string;
    voice_id: string;
    model_id?: string;
    output_format?: string;
  };

  const voiceId = body.voice_id || "21m00Tcm4TlvDq8ikWAM";
  const modelId = body.model_id || "eleven_turbo_v2_5";
  const outputFormat = body.output_format || "mp3_44100_128";

  const response = await fetch(
    `https://api.elevenlabs.io/v1/text-to-speech/${voiceId}/stream?output_format=${outputFormat}`,
    {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        "xi-api-key": env.ELEVENLABS_API_KEY,
      },
      body: JSON.stringify({
        text: body.text,
        model_id: modelId,
        voice_settings: {
          stability: 0.5,
          similarity_boost: 0.75,
          style: 0.0,
          use_speaker_boost: true,
        },
      }),
    }
  );

  if (!response.ok) {
    // Don't leak upstream error details to the client
    console.error("ElevenLabs error:", response.status, await response.text());
    return new Response("Text-to-speech service error", {
      status: response.status,
      headers: corsHeaders(),
    });
  }

  return new Response(response.body, {
    status: 200,
    headers: {
      ...corsHeaders(),
      "Content-Type": "audio/mpeg",
      "Transfer-Encoding": "chunked",
    },
  });
}
