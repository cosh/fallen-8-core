// @ts-check
import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';
import mermaid from 'astro-mermaid';
import starlightLlmsTxt from 'starlight-llms-txt';
import starlightLinksValidator from 'starlight-links-validator';

// Fallen-8 documentation site.
// GitHub Project Pages: served under https://cosh.github.io/fallen-8-core/
export default defineConfig({
	site: 'https://cosh.github.io',
	base: '/fallen-8-core',
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
				starlightLlmsTxt(),
			],
			sidebar: [
				{
					label: 'Getting Started',
					items: [
						{ label: 'Running', slug: 'running' },
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
						{ label: 'Bulk import/export', slug: 'bulk-import-export' },
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
					items: [{ label: 'F8 Studio', slug: 'studio' }],
				},
				{
					label: 'AI agents',
					items: [{ label: 'MCP server', slug: 'mcp-server' }],
				},
				{
					label: 'Reference',
					items: [
						{ label: 'API Reference', slug: 'api-reference' },
						{ label: 'REST API', slug: 'rest-api' },
						{ label: 'Architecture', slug: 'architecture' },
					],
				},
				{
					label: 'Help',
					items: [
						{ label: 'Troubleshooting', slug: 'troubleshooting' },
						{
							label: 'Debugging (contributing)',
							link: 'https://github.com/cosh/fallen-8-core/blob/main/DEBUGGING.md',
							attrs: { target: '_blank', rel: 'noopener' },
						},
						{ label: 'License', slug: 'license' },
					],
				},
			],
		}),
	],
});
