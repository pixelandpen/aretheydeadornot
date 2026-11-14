import { useEffect, useState, type FormEvent } from 'react';
import {
  fetchInMemory,
  fetchPersonByQid,
  fetchRecentDeaths,
  searchPerson,
} from './api';
import type { PersonDto, SearchResponse } from './types';
import './App.css';

function formatDate(iso: string | null): string {
  if (!iso) return 'Unknown';
  const [y, m, d] = iso.split('-').map(Number);
  if (!y || !m || !d) return iso;
  return new Date(Date.UTC(y, m - 1, d)).toLocaleDateString('en-US', {
    year: 'numeric',
    month: 'short',
    day: 'numeric',
    timeZone: 'UTC',
  });
}

export function App() {
  const [page, setPage] = useState<'home' | 'about'>(() =>
    window.location.pathname === '/about' ? 'about' : 'home',
  );
  const [query, setQuery] = useState('');
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [result, setResult] = useState<SearchResponse | null>(null);

  const [recentDeaths, setRecentDeaths] = useState<PersonDto[]>([]);
  const [inMemory, setInMemory] = useState<PersonDto[]>([]);
  const [listsLoading, setListsLoading] = useState(true);

  useEffect(() => {
    const onPopState = () => {
      setPage(window.location.pathname === '/about' ? 'about' : 'home');
    };
    window.addEventListener('popstate', onPopState);
    return () => window.removeEventListener('popstate', onPopState);
  }, []);

  function navigate(next: 'home' | 'about') {
    const path = next === 'about' ? '/about' : '/';
    if (window.location.pathname !== path) {
      window.history.pushState({}, '', path);
    }
    setPage(next);
  }

  useEffect(() => {
    Promise.all([fetchRecentDeaths(8), fetchInMemory(3, 8)])
      .then(([recent, memorial]) => {
        setRecentDeaths(recent.items);
        setInMemory(memorial.items);
      })
      .catch(() => {
        // Non-fatal if DB hasn't been seeded yet
      })
      .finally(() => setListsLoading(false));
  }, []);

  async function handleSubmit(e: FormEvent) {
    e.preventDefault();
    const trimmed = query.trim();
    if (!trimmed) return;
    setLoading(true);
    setError(null);
    try {
      const data = await searchPerson(trimmed);
      setResult(data);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Something went wrong.');
    } finally {
      setLoading(false);
    }
  }

  async function handleSelectQid(qid: string) {
    setLoading(true);
    setError(null);
    try {
      const data = await fetchPersonByQid(qid);
      setResult(data);
      setQuery(data.person?.name ?? '');
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Lookup failed.');
    } finally {
      setLoading(false);
    }
  }

  return (
    <div className="container">
      <nav className="top-nav" aria-label="Primary">
        <button
          type="button"
          className={`nav-link ${page === 'home' ? 'active' : ''}`}
          onClick={() => navigate('home')}
        >
          Search
        </button>
        <button
          type="button"
          className={`nav-link ${page === 'about' ? 'active' : ''}`}
          onClick={() => navigate('about')}
        >
          About This Site
        </button>
      </nav>

      {page === 'about' ? (
        <main className="card about-card">
          <h1>About This Site</h1>
          <p>
            Years ago, before Internet search and Google dominance was a thing, I was chatting with
            my brother about a certain celebrity (who it was is lost to the mists of time). One of
            us asked, &ldquo;I wonder if they are still alive?&rdquo; and the other jokingly said,
            &ldquo;We need a website called amideadornot.com we can ask!&rdquo; (At that point in
            time, amihotornot.com was all the rage.)
          </p>
          <p>
            So, when I wanted to learn some new skills around Microsoft Azure deployments, .NET
            APIs, Redis caching, and React, I decided to make the site. Within several hours,
            Google Antigravity had a working prototype running in Docker containers on my laptop,
            and a little after that, I had it working in the cloud. What a time to be alive (no pun
            intended haha)!
          </p>
          <p>
            You can see the source code at{' '}
            <a
              href="https://github.com/pixelandpen/aretheydeadornot"
              target="_blank"
              rel="noreferrer"
            >
              https://github.com/pixelandpen/aretheydeadornot
            </a>
            . This is totally for fun and has no commercial value, but I&rsquo;m totally open to
            pull requests for the repo so I can continue learning.
          </p>
          <button type="button" className="back-btn" onClick={() => navigate('home')}>
            &larr; Back to Search
          </button>
        </main>
      ) : (
        <>
          <header className="hero">
        <h1>Are They Dead Or Not?</h1>
        <p className="subtitle">
          Instant Wikidata lookup backed by PostgreSQL &amp; Redis HybridCache
        </p>

        <form className="search-form" onSubmit={handleSubmit}>
          <input
            type="text"
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            placeholder="Enter a celebrity name (e.g. Michael Jackson, Dolly Parton)..."
            aria-label="Celebrity name"
          />
          <button type="submit" disabled={loading || !query.trim()}>
            {loading ? 'Checking...' : 'Check Status'}
          </button>
        </form>
      </header>

      {error && <div className="alert error">{error}</div>}

      {result && (
        <section className="result-section">
          {!result.found || !result.person ? (
            <div className="card not-found">
              <h2>No match found for &ldquo;{result.query}&rdquo;</h2>
              <p>Try checking the spelling or searching by full name.</p>
            </div>
          ) : (
            <div className={`card result-card ${result.person.isDead ? 'dead' : 'alive'}`}>
              <div className="status-row">
                <span className={`badge ${result.person.isDead ? 'badge-dead' : 'badge-alive'}`}>
                  {result.person.isDead ? 'DECEASED' : 'ALIVE'}
                </span>
                <span className="latency">{result.queryTimeMs} ms</span>
              </div>

              <h2>{result.person.name}</h2>

              {result.person.occupations.length > 0 && (
                <p className="occupations">{result.person.occupations.join(' • ')}</p>
              )}

              <dl className="facts">
                <div>
                  <dt>Born</dt>
                  <dd>{formatDate(result.person.birthDate)}</dd>
                </div>
                {result.person.isDead && (
                  <div>
                    <dt>Died</dt>
                    <dd>{formatDate(result.person.deathDate)}</dd>
                  </div>
                )}
                {result.person.age !== null && (
                  <div>
                    <dt>{result.person.isDead ? 'Age at Death' : 'Age'}</dt>
                    <dd>{result.person.age}</dd>
                  </div>
                )}
                <div>
                  <dt>Wikidata</dt>
                  <dd>
                    <a href={result.person.wikidataUrl} target="_blank" rel="noreferrer">
                      {result.person.qid}
                    </a>
                  </dd>
                </div>
              </dl>

              {result.didYouMean.length > 0 && (
                <div className="did-you-mean">
                  <span>Did you mean:</span>
                  <div className="chips">
                    {result.didYouMean.map((cand) => (
                      <button
                        key={cand.qid}
                        type="button"
                        className="chip"
                        onClick={() => handleSelectQid(cand.qid)}
                      >
                        {cand.name}
                        {cand.occupations[0] ? ` (${cand.occupations[0]})` : ''}
                        {' · '}
                        {cand.isDead ? 'Dead' : 'Alive'}
                      </button>
                    ))}
                  </div>
                </div>
              )}
            </div>
          )}
        </section>
      )}

      <div className="panels">
        <section className="panel">
          <h3>Recent Deaths</h3>
          {listsLoading ? (
            <p className="muted">Loading...</p>
          ) : recentDeaths.length === 0 ? (
            <p className="muted">No records available yet.</p>
          ) : (
            <ul className="person-list">
              {recentDeaths.map((p) => (
                <li key={p.qid}>
                  <button type="button" className="list-btn" onClick={() => handleSelectQid(p.qid)}>
                    <span className="person-name">{p.name}</span>
                    <span className="person-meta">
                      Died {formatDate(p.deathDate)}
                      {p.occupations[0] ? ` · ${p.occupations[0]}` : ''}
                    </span>
                  </button>
                </li>
              ))}
            </ul>
          )}
        </section>

        <section className="panel">
          <h3>In Memory (±3 Days)</h3>
          {listsLoading ? (
            <p className="muted">Loading...</p>
          ) : inMemory.length === 0 ? (
            <p className="muted">No anniversaries in this window.</p>
          ) : (
            <ul className="person-list">
              {inMemory.map((p) => (
                <li key={p.qid}>
                  <button type="button" className="list-btn" onClick={() => handleSelectQid(p.qid)}>
                    <span className="person-name">{p.name}</span>
                    <span className="person-meta">
                      {formatDate(p.deathDate)}
                      {p.occupations[0] ? ` · ${p.occupations[0]}` : ''}
                    </span>
                  </button>
                </li>
              ))}
            </ul>
          )}
        </section>
      </div>
        </>
      )}
    </div>
  );
}

export default App;
