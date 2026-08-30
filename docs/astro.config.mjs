// @ts-check
import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';
import mermaid from 'astro-mermaid';
import starlightLlmsTxt from 'starlight-llms-txt';
import starlightLinksValidator from 'starlight-links-validator';

// Fallen-8 documentation site.
// GitHub Pages behind the custom domain https://docs.fallen-8.com/, which serves the site from
// the root: there is deliberately no Astro `base`, so internal links are written root-relative
// as `/<page>/`. The hostname is pinned in `public/CNAME` so a redeploy cannot drop it.
export default defineConfig({
	site: 'https://docs.fallen-8.com',
	integrations: [
		// astro-mermaid renders ```mermaid fences client-side; it must run before Starlight
		// so its rehype step claims those code blocks ahead of Expressive Code.
		mermaid({ theme: 'default', autoTheme: true }),
		starlight({
			title: 'Fallen-8',
			logo: {
				light: './src/assets/F8Black.svg',
				dark: './src/assets/F8White.svg',
				replacesTitle: true,
			},
			favicon: '/favicon.ico',
			// Drop Expressive Code's window "frame" (the fake terminal titlebar with its three
			// traffic-light dots, and the editor file-tab). The Tabs labels already say which
			// shell a snippet is, so the chrome is redundant noise. The copy button stays.
			expressiveCode: { defaultProps: { frame: 'none' } },
			customCss: ['./src/styles/mermaid-zoom.css'],
			components: {
				// adds a click-to-zoom lightbox for Mermaid diagrams
				Head: './src/components/Head.astro',
			},
			social: [
				{ icon: 'github', label: 'GitHub', href: 'https://github.com/cosh/fallen-8-core' },
			],
			plugins: [
				// Fail the build on broken internal links. Literal localhost URLs that appear in
				// tables (service maps) are intentional and not navigable, so they are excluded.
				starlightLinksValidator({
					exclude: [
						'http://localhost:*',
						'http://localhost:*/**',
						'http://127.0.0.1:*',
						'http://127.0.0.1:*/**',
					],
				}),
				// llms.txt for agents (https://llmstxt.org/). Left unconfigured this plugin emits a
				// bare stub and an llms-small.txt only ~16% under the full corpus, giving an agent no
				// reason to pick the abridged set. So both halves are set explicitly: `description`
				// and `details` give llms.txt something to say, `customSets` publish the curated
				// per-topic subsets, and `exclude` plus `minify` are what actually make the abridged
				// set small. Set labels track the `sidebar` groups below; keep them in step.
				starlightLlmsTxt({
					description:
						'Fallen-8 is an in-memory graph database written in C# (.NET 10). It holds a directed property graph, mutates it only through transactions, and answers path-finding, subgraph-pattern, vector and full-text queries either in process as a library or over a versioned REST API. Traversal filters and costs are small C# delegate fragments compiled at runtime rather than a bespoke query language. One deployment is a collection of namespaces, each an isolated graph owning one engine.',
					details: `Reading notes:

- Every mutation goes through a transaction: build one (\`CreateVerticesTransaction\`, \`CreateEdgesTransaction\`, ...), enqueue it, then wait for completion. Reads go straight through the read interface and need no transaction.
- The REST path and subgraph APIs take filter/cost predicates as C# lambda source strings, e.g. \`return (v) => v.Label == "person";\`, compiled with Roslyn at request time. A fragment runs in process with full trust, so authentication is the only boundary. Stored queries are the pre-compiled, invoke-by-name alternative.
- Every namespace-scoped route also answers under \`/ns/{ns}/...\`; a bare URL targets the reserved \`default\` namespace.
- The MCP server (for AI agents) and the integrations runtime (for reading systems on the operator's own network) are separate deployables that reach a graph over the public REST API only, never in process.`,
					customSets: [
						{
							label: 'Getting started',
							description:
								'run the engine or embed it as a library, and secure what you expose',
							paths: ['index', 'running', 'configuration', 'library', 'security', 'samples'],
						},
						{
							label: 'Graph model and queries',
							description:
								'the property graph, delegate fragments, path finding, subgraph patterns, analytics, stored queries and indexes',
							paths: [
								'graph-model',
								'delegates',
								'path-finding',
								'subgraphs',
								'graph-analytics',
								'stored-queries',
								'indexes',
							],
						},
						{
							label: 'Semantic and vector search',
							description:
								'element embeddings, vector indexes, semantic traversal, the model providers behind embeddings and chat, and the semantic layer over unstructured sources',
							paths: [
								'vector-search',
								'semantic-traversal',
								'model-providers',
								'nahil',
								'unstructured-ingestion',
							],
						},
						{
							label: 'REST API and data movement',
							description:
								'the HTTP surface, namespaces, bulk import/export, the change feed, save games and network integrations',
							paths: [
								'rest-api',
								'api-reference',
								'namespaces',
								'bulk-import-export',
								'change-feed',
								'save-games',
								'integrations',
							],
						},
						{
							label: 'AI agents',
							description: 'the MCP server tool surface and auth modes, plus NL assist and fine-tuning',
							paths: ['mcp-server', 'nl-assist'],
						},
						{
							label: 'F8 Studio',
							description:
								'the browser UI, its standalone deployment, embedding it in a host app, and the benchmark harness',
							paths: ['studio', 'standalone-ui', 'embed-studio', 'embed-scenarios', 'benchmark'],
						},
						{
							label: 'Operations and architecture',
							description:
								'how the layers and deployables fit, plus metrics/tracing, capacity, troubleshooting and local debugging',
							paths: [
								'architecture',
								'observability',
								'capacity-and-performance',
								'troubleshooting',
								'debugging',
							],
						},
						{
							label: 'Plugins',
							description: 'writing a path, subgraph or index plugin and registering it',
							paths: ['plugins', 'plugin-registration'],
						},
					],
					// Dropped from llms-small.txt only (llms-full.txt keeps everything). The abridged
					// set answers "how do I use this graph database", so the GUI tour, the sample
					// gallery walkthroughs, the ops/perf pages and the meta pages come out; every
					// engine, query and REST page stays.
					exclude: [
						'studio',
						'standalone-ui',
						'embed-studio',
						'embed-scenarios',
						'benchmark',
						'samples',
						'observability',
						'capacity-and-performance',
						'troubleshooting',
						'debugging',
						'nl-assist',
						'license',
					],
					// note/tip/details/whitespace already default to true; these two do not.
					minify: { caution: true, danger: true },
					optionalLinks: [
						{
							label: 'Source repository',
							url: 'https://github.com/cosh/fallen-8-core',
							description:
								'the engine, the REST app, the MCP server, the integrations runtime and the feature record behind each page',
						},
						{
							label: 'MIT license',
							url: 'https://github.com/cosh/fallen-8-core/blob/main/LICENSE',
							description: 'the terms the code and these docs ship under',
						},
					],
				}),
			],
			sidebar: [
				{
					label: 'Getting Started',
					items: [
						{ label: 'Running', slug: 'running' },
						{ label: 'Configuration', slug: 'configuration' },
						{ label: 'Use as a library', slug: 'library' },
						{ label: 'Security', slug: 'security' },
					],
				},
				{
					label: 'Samples',
					items: [{ label: 'Sample gallery', slug: 'samples' }],
				},
				{
					label: 'Features',
					items: [
						{ label: 'Graph model', slug: 'graph-model' },
						{ label: 'Delegates', slug: 'delegates' },
						{ label: 'Path finding', slug: 'path-finding' },
						{ label: 'Subgraphs', slug: 'subgraphs' },
						{ label: 'Graph analytics', slug: 'graph-analytics' },
						{ label: 'Stored queries', slug: 'stored-queries' },
						{ label: 'Indexes', slug: 'indexes' },
						{ label: 'Vector search', slug: 'vector-search' },
						{ label: 'Semantic traversal', slug: 'semantic-traversal' },
						{ label: 'Model providers', slug: 'model-providers' },
						{ label: 'Nahil', slug: 'nahil' },
						{ label: 'Semantic layer', slug: 'unstructured-ingestion' },
						{ label: 'Bulk import/export', slug: 'bulk-import-export' },
						{ label: 'Integrations', slug: 'integrations' },
						{ label: 'Change feed', slug: 'change-feed' },
						{ label: 'Save games', slug: 'save-games' },
						{ label: 'Namespaces', slug: 'namespaces' },
						{ label: 'Observability', slug: 'observability' },
						{ label: 'Plugins', slug: 'plugins' },
						{ label: 'Plugin registration', slug: 'plugin-registration' },
					],
				},
				{
					label: 'F8 Studio',
					items: [
						{ label: 'F8 Studio', slug: 'studio' },
						{ label: 'Standalone deployment', slug: 'standalone-ui' },
						{ label: 'Embed in a host app', slug: 'embed-studio' },
						{ label: 'Embed scenarios', slug: 'embed-scenarios' },
						{ label: 'Benchmark', slug: 'benchmark' },
					],
				},
				{
					label: 'AI agents',
					items: [
						{ label: 'MCP server', slug: 'mcp-server' },
						{ label: 'NL assist and fine-tuning', slug: 'nl-assist' },
					],
				},
				{
					label: 'Reference',
					items: [
						{ label: 'API Reference', slug: 'api-reference' },
						{ label: 'REST API', slug: 'rest-api' },
						{ label: 'Architecture', slug: 'architecture' },
						{ label: 'Capacity and performance', slug: 'capacity-and-performance' },
					],
				},
				{
					label: 'Help',
					items: [
						{ label: 'Troubleshooting', slug: 'troubleshooting' },
						{ label: 'Debugging in VS Code', slug: 'debugging' },
						{ label: 'License', slug: 'license' },
					],
				},
			],
		}),
	],
});
