import path from 'node:path'
import { defineConfig } from '@playwright/test'

// A private copy of the system for the browser tests: its own API (in-memory database, own port and build folder)
// and its own web server, so the tests never touch your development database or a running dev API.
export const API_PORT = 5299
export const WEB_PORT = 5273

export default defineConfig({
  testDir: './e2e',
  fullyParallel: false,
  workers: 1,
  retries: 0,
  timeout: 60_000,
  expect: { timeout: 10_000 },
  reporter: [['list']],
  use: {
    baseURL: `http://localhost:${WEB_PORT}`,
    // Fake camera and microphone so rooms can be joined without hardware or a permission prompt.
    launchOptions: { args: ['--use-fake-device-for-media-stream', '--use-fake-ui-for-media-stream'] },
    permissions: ['camera', 'microphone'],
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  webServer: [
    {
      // Built into its own folder so a running development API (which locks its own build output) does not get in the way.
      command: 'dotnet build ../Lms.Api -o .e2e/api -v q && cd .e2e/api && dotnet Lms.Api.dll',
      url: `http://localhost:${API_PORT}/health`,
      timeout: 240_000,
      reuseExistingServer: false,
      env: {
        ASPNETCORE_ENVIRONMENT: 'Development',
        Database__Provider: 'InMemory',
        Kestrel__Endpoints__Http__Url: `http://localhost:${API_PORT}`,
        LiveClasses__FrontendBaseUrl: `http://localhost:${WEB_PORT}`,   // where the built-in provider's join links point
        // Uploaded videos are converted for streaming with FFmpeg run in a container (needs Docker). E2E_NO_FFMPEG=1 turns conversion off.
        Videos__Processing__Enabled: process.env.E2E_NO_FFMPEG === '1' ? 'false' : 'true',
        Videos__Ffmpeg__DockerImage: process.env.E2E_FFMPEG_IMAGE ?? 'linuxserver/ffmpeg',
        // Class recordings need LiveKit's recording service (scripts/livekit-dev.ps1). E2E_EGRESS=1 turns them on for the browser tests.
        LiveKit__Egress__Enabled: process.env.E2E_EGRESS === '1' ? 'true' : 'false',
        LiveKit__Egress__Destination: 'Local',
        LiveKit__Egress__LocalDirectory: path.resolve(process.cwd(), '.e2e', 'egress'),
        LiveKit__Egress__ContainerPath: '/out',
        Integrations__AllowInsecureLiveClassHosts: 'true',   // lets the LiveKit test use a local ws:// server
        RateLimiting__LoginPermitsPerMinute: '10000',
        RateLimiting__ApiPermitsPerMinute: '100000',
      },
    },
    {
      command: `npm run dev -- --port ${WEB_PORT} --strictPort`,
      url: `http://localhost:${WEB_PORT}`,
      timeout: 120_000,
      reuseExistingServer: false,
      env: { LMS_API_URL: `http://localhost:${API_PORT}` },
    },
  ],
})
