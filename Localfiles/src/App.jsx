import { useEffect, useState } from 'react';
import DocViewer, { DocViewerRenderers } from '@cyntler/react-doc-viewer';
import '@cyntler/react-doc-viewer/dist/index.css';

const getTokenFromQueryString = () => {
    if (typeof window === 'undefined') {
        return '';
    }

    return new URLSearchParams(window.location.search).get('token') || '';
};

const getFallbackDocuments = () => [
    { uri: '/Designer_AI_Tagging.pdf', fileName: 'Designer_AI_Tagging.pdf' },
    { uri: '/HighLevelAgentScriptFlow.docx', fileName: 'HighLevelAgentScriptFlow.docx' },
    { uri: '/sample.docx', fileName: 'sample.docx' },
    { uri: '/sample.pptx', fileName: 'sample.pptx' },
];

export default function App() {
    const token = getTokenFromQueryString();
    const functionBaseUrl = import.meta.env.VITE_FUNCTION_BASE_URL || '';
    const [documents, setDocuments] = useState(() => token ? [] : getFallbackDocuments());
    const [activeDocument, setActiveDocument] = useState(() => token ? undefined : getFallbackDocuments()[0]);
    const [isLoading, setIsLoading] = useState(Boolean(token));
    const [resolveError, setResolveError] = useState('');
    const [thumbnailUrl, setThumbnailUrl] = useState('');

    useEffect(() => {
        if (!token) {
            return;
        }

        if (!functionBaseUrl) {
            setResolveError('The document service URL is not configured.');
            setIsLoading(false);
            return;
        }

        let cancelled = false;

        const resolveDocumentFromFunction = async () => {
            setIsLoading(true);
            setResolveError('');

            try {
                const response = await fetch(
                    `${functionBaseUrl}/document?token=${encodeURIComponent(token)}`,
                    {
                        headers: { Accept: 'application/json' },
                    },
                );

                if (!response.ok) {
                    throw new Error(`Function returned ${response.status}`);
                }

                const data = await response.json();

                if (cancelled) {
                    return;
                }

                if (!data?.uri) {
                    throw new Error('The document service did not return a document URL.');
                }

                const fileName = data.fileName || 'document';
                const isPdf = /\.pdf$/i.test(fileName) || /\.pdf$/i.test(new URL(data.uri, window.location.origin).pathname);
                const resolvedDocuments = [{
                    uri: data.uri,
                    fileName,
                    ...(isPdf ? { fileType: 'pdf' } : {}),
                }];
                setDocuments(resolvedDocuments);
                setActiveDocument(resolvedDocuments[0]);
                setThumbnailUrl(/^https:\/\//i.test(data.thumbnailUrl || '') ? data.thumbnailUrl : '');
            } catch (error) {
                console.error('Unable to resolve document from Azure Function:', error);
                if (!cancelled) {
                    setResolveError('Unable to resolve this document. Please check the link or contact the document owner.');
                }
            } finally {
                if (!cancelled) {
                    setIsLoading(false);
                }
            }
        };

        resolveDocumentFromFunction();

        return () => {
            cancelled = true;
        };
    }, [functionBaseUrl, token]);

    const brandName = import.meta.env.VITE_BRAND_NAME || 'Document Portal';
    const termsUrl = import.meta.env.VITE_TERMS_URL || '#';

    const handleDownload = () => {
        if (!activeDocument?.uri) {
            return;
        }

        const link = document.createElement('a');
        link.href = activeDocument.uri;
        link.target = '_blank';
        link.rel = 'noopener noreferrer';
        link.download = activeDocument.fileName || 'document';
        link.click();
    };

    return (
        <div className="app-shell">
            <header className="app-header">
                <div className="brand-block">
                    <div className="brand-mark">DP</div>
                    <div className="brand-copy">
                        <span className="brand-name">{brandName}</span>
                        <small>Secure document sharing</small>
                    </div>
                </div>

                <div className="header-actions">
                    {thumbnailUrl && (
                        <img
                            className="header-thumbnail"
                            src={thumbnailUrl}
                            alt={`Preview of ${activeDocument?.fileName || 'document'}`}
                            referrerPolicy="no-referrer"
                            onError={() => setThumbnailUrl('')}
                        />
                    )}
                    <span className="token-pill">
                        {isLoading ? 'Resolving document...' : token ? 'Secure token session' : 'Demo session'}
                    </span>
                    <button type="button" className="download-button" onClick={handleDownload} disabled={!activeDocument?.uri}>
                        Download
                    </button>
                </div>
            </header>

            <main className="app-content">
                <section className="document-panel">
                    <div className="document-header-row">
                        <div>
                            <p className="eyebrow">Current document</p>
                            <h1>{activeDocument?.fileName || 'Document'}</h1>
                        </div>
                        <span className="document-badge">
                            {documents.length > 1 ? 'Multiple files' : 'Single file'}
                        </span>
                    </div>

                    <div className="document-picker" aria-label="Document picker">
                        {documents.map((doc) => (
                            <button
                                key={doc.fileName}
                                type="button"
                                className={
                                    activeDocument?.fileName === doc.fileName
                                        ? 'document-picker-button active'
                                        : 'document-picker-button'
                                }
                                onClick={() => setActiveDocument(doc)}
                            >
                                {doc.fileName}
                            </button>
                        ))}
                    </div>

                    <div className="viewer-wrap">
                        {isLoading ? (
                            <p role="status">Resolving document...</p>
                        ) : resolveError ? (
                            <p role="alert">{resolveError}</p>
                        ) : <DocViewer
                            documents={documents}
                            activeDocument={activeDocument}
                            onDocumentChange={setActiveDocument}
                            pluginRenderers={DocViewerRenderers}
                            config={{
                                header: { disableHeader: true },
                                noRenderer: {
                                    overrideComponent: ({ fileName }) => (
                                        <div className="unsupported-message">
                                            <h3>Preview unavailable</h3>
                                            <p>
                                                This file type is not supported by the browser preview or needs a
                                                public SharePoint URL or Azure Function proxy.
                                            </p>
                                            <strong>{fileName || 'Document'}</strong>
                                        </div>
                                    ),
                                },
                            }}
                            style={{ width: '100%', height: '100%' }}
                        />}
                    </div>
                </section>
            </main>

            <footer className="app-footer">
                <span>© {new Date().getFullYear()} {brandName}</span>
                <a href={termsUrl} target="_blank" rel="noreferrer">
                    Terms & Conditions
                </a>
            </footer>
        </div>
    );
}
