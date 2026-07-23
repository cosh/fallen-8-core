/**
 * movie-night — a recommendation graph (feature sample-graphs). Iconic films, their
 * genres, and seeded viewers whose taste profiles create real community structure.
 * Movies carry a Wikipedia poster URL (resolved once at build time — the canvas renders
 * it as the node image) and an embedded plot for semantic search. Ratings weight the
 * viewer→movie edges. No embedding work happens at ingest: the plot vectors are baked in.
 */

import { buildJsonlGraph, prop, type JsonlEdge, type JsonlVertex } from "../../src/lib/jsonlGraph";
import {
  boundVectorIndexRecipe,
  embeddingProperties,
  embedTexts,
  seededRandom,
  type BuiltSample,
} from "./shared";

interface Movie {
  title: string;
  year: number;
  wiki: string; // exact Wikipedia page title for poster + reliability
  genres: string[];
  plot: string;
}

// A curated, well-known set spanning genres so communities and recommendations are
// recognizable. `wiki` pins the page so poster resolution is deterministic.
const MOVIES: Movie[] = [
  { title: "Inception", year: 2010, wiki: "Inception", genres: ["Sci-Fi", "Thriller"], plot: "A thief who steals corporate secrets through dream-sharing technology is tasked with planting an idea into a target's subconscious." },
  { title: "The Matrix", year: 1999, wiki: "The Matrix", genres: ["Sci-Fi", "Action"], plot: "A hacker discovers reality is a simulation and joins a rebellion against the machines that enslave humanity." },
  { title: "Interstellar", year: 2014, wiki: "Interstellar", genres: ["Sci-Fi", "Drama"], plot: "Explorers travel through a wormhole near Saturn in search of a new home for a dying Earth." },
  { title: "Blade Runner", year: 1982, wiki: "Blade Runner", genres: ["Sci-Fi", "Thriller"], plot: "A blade runner hunts rogue bioengineered replicants in a rain-soaked future Los Angeles." },
  { title: "Arrival", year: 2016, wiki: "Arrival (film)", genres: ["Sci-Fi", "Drama"], plot: "A linguist races to communicate with mysterious alien visitors before global tensions erupt into war." },
  { title: "The Dark Knight", year: 2008, wiki: "The Dark Knight", genres: ["Action", "Crime"], plot: "Batman faces the Joker, an anarchic criminal who plunges Gotham City into chaos." },
  { title: "Mad Max: Fury Road", year: 2015, wiki: "Mad Max: Fury Road", genres: ["Action", "Sci-Fi"], plot: "On a post-apocalyptic desert highway, a woman rebels against a tyrant with a band of escaping captives." },
  { title: "Gladiator", year: 2000, wiki: "Gladiator (2000 film)", genres: ["Action", "Drama"], plot: "A betrayed Roman general becomes a gladiator seeking revenge against the corrupt emperor who murdered his family." },
  { title: "The Godfather", year: 1972, wiki: "The Godfather", genres: ["Crime", "Drama"], plot: "The aging patriarch of a crime dynasty transfers control of his empire to his reluctant youngest son." },
  { title: "Pulp Fiction", year: 1994, wiki: "Pulp Fiction", genres: ["Crime", "Drama"], plot: "The lives of hit men, a boxer, and a gangster's wife interweave in four tales of violence and redemption." },
  { title: "Goodfellas", year: 1990, wiki: "Goodfellas", genres: ["Crime", "Drama"], plot: "The rise and fall of a mob associate across three decades of the New York Italian-American mafia." },
  { title: "The Shawshank Redemption", year: 1994, wiki: "The Shawshank Redemption", genres: ["Drama"], plot: "A banker wrongly imprisoned for murder forms a lasting friendship and quietly plots his escape over decades." },
  { title: "Forrest Gump", year: 1994, wiki: "Forrest Gump", genres: ["Drama", "Romance"], plot: "A kind-hearted man with a low IQ inadvertently witnesses and shapes decades of American history." },
  { title: "Fight Club", year: 1999, wiki: "Fight Club", genres: ["Drama", "Thriller"], plot: "An insomniac office worker and a soap maker form an underground fight club that spirals out of control." },
  { title: "Parasite", year: 2019, wiki: "Parasite (2019 film)", genres: ["Thriller", "Drama"], plot: "A poor family schemes to infiltrate a wealthy household, with darkly comic and violent consequences." },
  { title: "Whiplash", year: 2014, wiki: "Whiplash (2014 film)", genres: ["Drama"], plot: "A young jazz drummer is pushed to the brink by an abusive conservatory instructor." },
  { title: "The Silence of the Lambs", year: 1991, wiki: "The Silence of the Lambs (film)", genres: ["Thriller", "Crime"], plot: "An FBI trainee seeks the help of an imprisoned cannibal to catch another serial killer." },
  { title: "Se7en", year: 1995, wiki: "Seven (1995 film)", genres: ["Thriller", "Crime"], plot: "Two detectives hunt a serial killer who uses the seven deadly sins as his motives." },
  { title: "The Prestige", year: 2006, wiki: "The Prestige (film)", genres: ["Thriller", "Drama"], plot: "Two rival magicians in Victorian London engage in an escalating and deadly battle of one-upmanship." },
  { title: "Jurassic Park", year: 1993, wiki: "Jurassic Park (film)", genres: ["Adventure", "Sci-Fi"], plot: "Cloned dinosaurs break loose in a theme park, trapping the visitors on the island." },
  { title: "Raiders of the Lost Ark", year: 1981, wiki: "Raiders of the Lost Ark", genres: ["Adventure", "Action"], plot: "An archaeologist races the Nazis to recover the biblical Ark of the Covenant." },
  { title: "The Lord of the Rings: The Fellowship of the Ring", year: 2001, wiki: "The Lord of the Rings: The Fellowship of the Ring", genres: ["Fantasy", "Adventure"], plot: "A hobbit inherits a powerful ring and sets out with a fellowship to destroy it before it falls to a dark lord." },
  { title: "Spirited Away", year: 2001, wiki: "Spirited Away", genres: ["Animation", "Fantasy"], plot: "A young girl wanders into a spirit world and must work in a bathhouse to free her transformed parents." },
  { title: "Toy Story", year: 1995, wiki: "Toy Story", genres: ["Animation", "Comedy"], plot: "A cowboy doll grows jealous when a spaceman action figure becomes a boy's new favorite toy." },
  { title: "Spider-Man: Into the Spider-Verse", year: 2018, wiki: "Spider-Man: Into the Spider-Verse", genres: ["Animation", "Action"], plot: "A teenager becomes Spider-Man and teams up with spider-heroes from parallel universes." },
  { title: "Coco", year: 2017, wiki: "Coco (2017 film)", genres: ["Animation", "Fantasy"], plot: "A boy journeys to the Land of the Dead to uncover his family's history and his musical destiny." },
  { title: "The Grand Budapest Hotel", year: 2014, wiki: "The Grand Budapest Hotel", genres: ["Comedy", "Drama"], plot: "A legendary concierge and his protégé are framed for murder amid the theft of a priceless painting." },
  { title: "Superbad", year: 2007, wiki: "Superbad", genres: ["Comedy"], plot: "Two co-dependent high-school friends scramble to supply alcohol for a party before they graduate." },
  { title: "Groundhog Day", year: 1993, wiki: "Groundhog Day (film)", genres: ["Comedy", "Romance"], plot: "A cynical weatherman relives the same day over and over until he learns to become a better person." },
  { title: "La La Land", year: 2016, wiki: "La La Land", genres: ["Romance", "Drama"], plot: "A jazz pianist and an aspiring actress fall in love while chasing their dreams in Los Angeles." },
  { title: "Titanic", year: 1997, wiki: "Titanic (1997 film)", genres: ["Romance", "Drama"], plot: "A rich girl and a poor artist fall in love aboard the doomed maiden voyage of the RMS Titanic." },
  { title: "Eternal Sunshine of the Spotless Mind", year: 2004, wiki: "Eternal Sunshine of the Spotless Mind", genres: ["Romance", "Sci-Fi"], plot: "A couple undergo a procedure to erase each other from their memories after a painful breakup." },
  { title: "Alien", year: 1979, wiki: "Alien (film)", genres: ["Horror", "Sci-Fi"], plot: "The crew of a commercial spaceship is hunted by a deadly extraterrestrial creature." },
  { title: "The Shining", year: 1980, wiki: "The Shining (film)", genres: ["Horror", "Thriller"], plot: "A writer descends into madness while serving as winter caretaker of an isolated haunted hotel." },
  { title: "Get Out", year: 2017, wiki: "Get Out", genres: ["Horror", "Thriller"], plot: "A young man uncovers a disturbing secret when he visits his white girlfriend's family estate." },
  { title: "No Country for Old Men", year: 2007, wiki: "No Country for Old Men", genres: ["Crime", "Thriller"], plot: "A hunter's discovery of drug-deal cash unleashes a relentless killer across the Texas desert." },
  { title: "Dune", year: 2021, wiki: "Dune (2021 film)", genres: ["Sci-Fi", "Adventure"], plot: "A noble heir must navigate desert politics and prophecy to protect the most valuable resource in the galaxy." },
  { title: "Everything Everywhere All at Once", year: 2022, wiki: "Everything Everywhere All at Once", genres: ["Sci-Fi", "Comedy"], plot: "A laundromat owner discovers she must connect with parallel versions of herself to save the multiverse." },
  { title: "Amélie", year: 2001, wiki: "Amélie", genres: ["Romance", "Comedy"], plot: "A whimsical Parisian waitress secretly orchestrates small acts of kindness while finding her own love." },
  { title: "Casablanca", year: 1942, wiki: "Casablanca (film)", genres: ["Romance", "Drama"], plot: "A cynical nightclub owner must choose between love and virtue when an old flame arrives in wartime Morocco." },
];

/** Viewer taste archetypes → the genres they lean toward, creating community structure. */
const TASTE_PROFILES: Array<{ name: string; genres: string[] }> = [
  { name: "sci-fi buff", genres: ["Sci-Fi", "Action"] },
  { name: "arthouse regular", genres: ["Drama", "Romance"] },
  { name: "thriller junkie", genres: ["Thriller", "Crime"] },
  { name: "animation fan", genres: ["Animation", "Fantasy"] },
  { name: "comedy lover", genres: ["Comedy", "Romance"] },
  { name: "horror hound", genres: ["Horror", "Thriller"] },
  { name: "adventure seeker", genres: ["Adventure", "Action"] },
];

const VIEWER_COUNT = 140;

async function resolvePoster(wiki: string): Promise<string | null> {
  try {
    const url = `https://en.wikipedia.org/api/rest_v1/page/summary/${encodeURIComponent(wiki)}`;
    const response = await fetch(url, { headers: { Accept: "application/json" } });
    if (!response.ok) return null;
    const data = (await response.json()) as {
      thumbnail?: { source?: string };
      originalimage?: { source?: string };
    };
    return data.thumbnail?.source ?? data.originalimage?.source ?? null;
  } catch {
    return null;
  }
}

export async function buildMovieNight(): Promise<BuiltSample> {
  const rng = seededRandom(0x6d0713);

  const vertices: JsonlVertex[] = [];
  const edges: JsonlEdge[] = [];
  let edgeId = 1_000_000;

  // Ids: movies [0..M), genres next, viewers last — stable across rebuilds.
  const genreNames = [...new Set(MOVIES.flatMap((m) => m.genres))].sort();
  const movieIdByTitle = new Map<string, number>();
  const genreIdByName = new Map<string, number>();

  MOVIES.forEach((movie, index) => movieIdByTitle.set(movie.title, index));
  genreNames.forEach((name, index) => genreIdByName.set(name, MOVIES.length + index));
  const firstViewerId = MOVIES.length + genreNames.length;

  // Posters (resolved once) + plot embeddings (baked in).
  console.log("  resolving posters…");
  const posters = await Promise.all(MOVIES.map((m) => resolvePoster(m.wiki)));
  const posterHits = posters.filter(Boolean).length;
  console.log(`  posters: ${posterHits}/${MOVIES.length} resolved (rest fall back to 🎬)`);

  const { vectors, model, dimension } = await embedTexts(
    MOVIES.map((m) => `${m.title} (${m.year}). ${m.genres.join(", ")}. ${m.plot}`),
  );

  MOVIES.forEach((movie, index) => {
    vertices.push({
      id: index,
      label: "movie",
      properties: {
        title: prop.string(movie.title),
        year: prop.int32(movie.year),
        plot: prop.string(movie.plot),
        icon: prop.string(posters[index] ?? "🎬"),
        ...embeddingProperties(vectors[index], model),
      },
    });
  });

  genreNames.forEach((name) => {
    vertices.push({
      id: genreIdByName.get(name)!,
      label: "genre",
      properties: { name: prop.string(name), icon: prop.string("🏷️") },
    });
    // movie -> genre
  });
  for (const movie of MOVIES) {
    for (const genre of movie.genres) {
      edges.push({
        id: edgeId++,
        source: movieIdByTitle.get(movie.title)!,
        target: genreIdByName.get(genre)!,
        edgePropertyId: "belongsTo",
      });
    }
  }

  // Viewers rate movies weighted toward their taste genres → community structure.
  for (let v = 0; v < VIEWER_COUNT; v++) {
    const profile = TASTE_PROFILES[v % TASTE_PROFILES.length];
    const viewerId = firstViewerId + v;
    vertices.push({
      id: viewerId,
      label: "viewer",
      properties: {
        name: prop.string(`${profile.name} #${Math.floor(v / TASTE_PROFILES.length) + 1}`),
        icon: prop.string("👤"),
      },
    });

    const rated = new Set<number>();
    const targetRatings = 8 + Math.floor(rng() * 8); // 8..15
    let guard = 0;
    while (rated.size < targetRatings && guard++ < 200) {
      // 70% pick a movie in a favored genre, else anything → cross-community links exist.
      let movieIndex: number;
      if (rng() < 0.7) {
        const genre = profile.genres[Math.floor(rng() * profile.genres.length)];
        const inGenre = MOVIES.map((m, i) => (m.genres.includes(genre) ? i : -1)).filter((i) => i >= 0);
        movieIndex = inGenre[Math.floor(rng() * inGenre.length)];
      } else {
        movieIndex = Math.floor(rng() * MOVIES.length);
      }
      if (rated.has(movieIndex)) continue;
      rated.add(movieIndex);

      const favored = MOVIES[movieIndex].genres.some((g) => profile.genres.includes(g));
      // Favored-genre ratings skew high; others are more spread — realistic edge weights.
      const rating = favored
        ? 3.5 + rng() * 1.5
        : 1.5 + rng() * 3;
      edges.push({
        id: edgeId++,
        source: viewerId,
        target: movieIndex,
        edgePropertyId: "rated",
        properties: { rating: prop.double(Math.round(rating * 10) / 10) },
      });
    }
  }

  return {
    jsonl: buildJsonlGraph(vertices, edges),
    entry: {
      id: "movie-night",
      title: "Movie Night",
      emoji: "🎬",
      pitch:
        "Films, genres, and viewers with real taste communities — poster-image nodes, plot embeddings, and rating-weighted recommendations.",
      vertexCount: vertices.length,
      edgeCount: edges.length,
      badges: ["canvas", "path", "analytics", "semantic"],
      trySteps: [
        "Semantic search: 'mind-bending sci-fi about dreams' surfaces Inception; 'a haunted hotel' finds The Shining.",
        "Path → a 2-hop viewer → movie → viewer → movie chain is a recommendation (look ids up on the Browser screen).",
        "Analytics → PAGERANK ranks the canon; LABELPROPAGATION recovers the taste communities.",
        "Canvas → movie nodes show real posters ('icon'); edge width by 'rating', size by degree.",
      ],
      file: "movie-night.jsonl",
      styleConfig: {
        nodeColorMode: "label",
        nodeSizeMode: "degree",
        nodeImageProperty: "icon",
        edgeWidthMode: "property",
        edgeWidthProperty: "rating",
        edgeArrows: false,
      },
      indexRecipes: [boundVectorIndexRecipe(dimension)],
      embedding: { name: "default", model, dimension, metric: "Cosine" },
    },
  };
}
